using System;
using System.Runtime.InteropServices;

public sealed class PcscReader : IDisposable
{
    // ---- P/Invoke ----
    private const int SCARD_SCOPE_USER = 0;

    private const int SCARD_SHARE_SHARED = 2;
    private const int SCARD_PROTOCOL_T0 = 1;
    private const int SCARD_PROTOCOL_T1 = 2;
    private const int SCARD_LEAVE_CARD = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SCARD_IO_REQUEST
    { public int dwProtocol; public int cbPciLength; }

    private static SCARD_IO_REQUEST IOREQ_T0 = new SCARD_IO_REQUEST { dwProtocol = SCARD_PROTOCOL_T0, cbPciLength = 8 };
    private static SCARD_IO_REQUEST IOREQ_T1 = new SCARD_IO_REQUEST { dwProtocol = SCARD_PROTOCOL_T1, cbPciLength = 8 };

    [DllImport("winscard.dll")] private static extern int SCardEstablishContext(int scope, IntPtr r1, IntPtr r2, out IntPtr ctx);

    [DllImport("winscard.dll")] private static extern int SCardReleaseContext(IntPtr ctx);

    [DllImport("winscard.dll")] private static extern int SCardListReaders(IntPtr ctx, byte[] groups, byte[] readers, ref int size);

    [DllImport("winscard.dll", CharSet = CharSet.Auto)]
    private static extern int SCardConnect(IntPtr ctx, string reader, int share, int proto, out IntPtr hCard, out int activeProto);

    [DllImport("winscard.dll")] private static extern int SCardDisconnect(IntPtr card, int disposition);

    [DllImport("winscard.dll")] private static extern int SCardTransmit(IntPtr hCard, ref SCARD_IO_REQUEST pioSend, byte[] send, int sendLen, IntPtr recvPci, byte[] recv, ref int recvLen);

    private IntPtr _ctx = IntPtr.Zero;
    private readonly string _readerName;

    public PcscReader(string preferNameContains = "PICC")
    {
        var rc = SCardEstablishContext(SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out _ctx);
        if (rc != 0) throw new Exception($"SCardEstablishContext rc={rc}");

        // Список ридеров
        int size = 0;
        SCardListReaders(_ctx, null, null, ref size);
        if (size <= 2) throw new Exception("PC/SC readers not found");

        var buf = new byte[size];
        rc = SCardListReaders(_ctx, null, buf, ref size);
        if (rc != 0) throw new Exception($"SCardListReaders rc={rc}");

        var ascii = System.Text.Encoding.ASCII.GetString(buf, 0, Math.Max(0, size - 2)); // срезаем финальные два нуля
        var names = ascii.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0) throw new Exception("PC/SC readers not found");

        // Предпочтительно бесконтактный
        var idx = Array.FindIndex(names, n => n.IndexOf(preferNameContains, StringComparison.OrdinalIgnoreCase) >= 0);
        _readerName = (idx >= 0) ? names[idx] : names[0];
    }

    public string ReadUidHex(int retries = 10)
    {
        // Повторные попытки на случай Sharing Violation
        for (int i = 0; i < retries; i++)
        {
            IntPtr hCard;
            int proto;
            int rc = SCardConnect(_ctx, _readerName, SCARD_SHARE_SHARED, SCARD_PROTOCOL_T0 | SCARD_PROTOCOL_T1, out hCard, out proto);
            if (rc == 0)
            {
                try
                {
                    var io = (proto == SCARD_PROTOCOL_T0) ? IOREQ_T0 : IOREQ_T1;
                    byte[] apdu = { 0xFF, 0xCA, 0x00, 0x00, 0x00 }; // Get Data (UID)
                    byte[] recv = new byte[32];
                    int recvLen = recv.Length;

                    rc = SCardTransmit(hCard, ref io, apdu, apdu.Length, IntPtr.Zero, recv, ref recvLen);
                    if (rc != 0) throw new Exception($"SCardTransmit rc={rc}");
                    if (recvLen < 2) return null;

                    byte sw1 = recv[recvLen - 2], sw2 = recv[recvLen - 1];
                    if (sw1 == 0x90 && sw2 == 0x00)
                    {
                        int uidLen = recvLen - 2;
                        if (uidLen <= 0) return null;
                        byte[] uid = new byte[uidLen];
                        Array.Copy(recv, uid, uidLen);
                        return BitConverter.ToString(uid); // "04-AB-..."
                    }
                    else
                    {
                        throw new Exception($"APDU SW={sw1:X2} {sw2:X2}");
                    }
                }
                finally
                {
                    SCardDisconnect(hCard, SCARD_LEAVE_CARD);
                }
            }
            else if (rc == unchecked((int)0x8010000F)) // SCARD_E_SHARING_VIOLATION
            {
                System.Threading.Thread.Sleep(200);
                continue;
            }
            else if (rc == unchecked((int)0x8010000C)) // SCARD_E_NO_SMARTCARD
            {
                // нет карты в поле
                return null;
            }
            else
            {
                throw new Exception($"SCardConnect rc={rc}");
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_ctx != IntPtr.Zero)
        {
            SCardReleaseContext(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LibraryTerminal
{
    public sealed class Acr1281PcscReader : IDisposable
    {
        // ===== События =====
        public event Action<string> OnUid;

        // ===== Параметры =====
        private readonly string _preferNameContains;

        private readonly int _pollIntervalMs;
        private readonly int _connectRetries;
        private readonly int _busyRetryDelayMs;

        // ===== Служебное =====
        private CancellationTokenSource _cts;

        private Task _worker;

        public Acr1281PcscReader(
            string preferNameContains = "PICC",
            int pollIntervalMs = 250,
            int connectRetries = 10,
            int busyRetryDelayMs = 300)
        {
            _preferNameContains = preferNameContains;
            _pollIntervalMs = pollIntervalMs;
            _connectRetries = connectRetries;
            _busyRetryDelayMs = busyRetryDelayMs;
        }

        // ===== API =====
        public void Start()
        {
            if (_worker != null) return;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerAsync(_cts.Token));
        }

        public void Dispose()
        {
            try
            {
                if (_cts != null && !_cts.IsCancellationRequested) _cts.Cancel();
                _worker?.Wait(1000);
            } catch { /* ignore */ }
            finally
            {
                _worker = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        // ===== Основной цикл =====
        private async Task WorkerAsync(CancellationToken ct)
        {
            IntPtr ctx = IntPtr.Zero;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 1) Context
                    int rc = SCardEstablishContext(SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx);
                    if (rc != SCARD_S_SUCCESS)
                        throw new Exception($"SCardEstablishContext rc=0x{rc:X8}");

                    // 2) Список ридеров
                    string reader = PickPiccReader(ctx);
                    if (string.IsNullOrEmpty(reader))
                        throw new Exception("PC/SC readers not found");

                    // 3) Подключение с ретраями
                    bool connected = false;
                    IntPtr hCard = IntPtr.Zero;
                    int proto = 0;

                    for (int i = 0; i < _connectRetries && !ct.IsCancellationRequested; i++)
                    {
                        rc = SCardConnect(ctx, reader, SCARD_SHARE_SHARED, SCARD_PROTOCOL_T0 | SCARD_PROTOCOL_T1, out hCard, out proto);
                        if (rc == SCARD_S_SUCCESS)
                        {
                            connected = true;
                            break;
                        }
                        if (rc == SCARD_E_SHARING_VIOLATION)
                        {
                            await Task.Delay(_busyRetryDelayMs, ct);
                            continue;
                        }
                        if (rc == SCARD_E_NO_SMARTCARD)
                        {
                            // Карты нет — подождём и заново.
                            await Task.Delay(_pollIntervalMs, ct);
                            continue;
                        }
                        // Иные ошибки считаем фатальными для этой итерации
                        throw new Exception($"SCardConnect rc=0x{rc:X8}");
                    }

                    if (!connected)
                    {
                        await Task.Delay(_pollIntervalMs, ct);
                        continue;
                    }

                    try
                    {
                        // 4) APDU: FF CA 00 00 00 -> UID
                        var io = (proto == SCARD_PROTOCOL_T0) ? IOREQ_T0 : IOREQ_T1;
                        byte[] send = new byte[] { 0xFF, 0xCA, 0x00, 0x00, 0x00 };
                        byte[] recv = new byte[32];
                        int recvLen = recv.Length;

                        rc = SCardTransmit(hCard, ref io, send, send.Length, IntPtr.Zero, recv, ref recvLen);
                        if (rc == SCARD_S_SUCCESS && recvLen >= 2)
                        {
                            byte sw1 = recv[recvLen - 2];
                            byte sw2 = recv[recvLen - 1];
                            if (sw1 == 0x90 && sw2 == 0x00)
                            {
                                int uidLen = recvLen - 2;
                                if (uidLen > 0)
                                {
                                    var uid = new byte[uidLen];
                                    Array.Copy(recv, uid, uidLen);
                                    string uidHex = BitConverter.ToString(uid); // "04-AB-…"
                                    OnUidSafe(uidHex);
                                }
                            }
                            // Иначе — карта не поддерживает команду/ошибка, просто игнорируем
                        }
                    }
                    finally
                    {
                        // 5) Отключение от карты
                        SCardDisconnect(hCard, SCARD_LEAVE_CARD);
                    }
                } catch (OperationCanceledException) { /* stop */ } catch
                {
                    // Мягко спим и пробуем заново
                }
                finally
                {
                    if (ctx != IntPtr.Zero)
                    {
                        SCardReleaseContext(ctx);
                        ctx = IntPtr.Zero;
                    }
                }

                await Task.Delay(_pollIntervalMs, ct);
            }
        }

        private void OnUidSafe(string uid)
        {
            try { OnUid?.Invoke(uid); } catch { /* ignore user handlers */ }
        }

        private static string PickPiccReader(IntPtr ctx)
        {
            int size = 0;
            SCardListReaders(ctx, null, null, ref size);
            if (size <= 2) return null;

            byte[] buf = new byte[size];
            int rc = SCardListReaders(ctx, null, buf, ref size);
            if (rc != SCARD_S_SUCCESS) return null;

            // Multi-string ANSI (0x00 separators, ends with 0x00 0x00)
            string ascii = Encoding.ASCII.GetString(buf, 0, Math.Max(0, size - 2));
            string[] names = ascii.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);

            if (names.Length == 0) return null;

            // Предпочитаем PICC
            foreach (var n in names)
                if (n.IndexOf("PICC", StringComparison.OrdinalIgnoreCase) >= 0)
                    return n;

            return names[0];
        }

        // ===== P/Invoke winscard =====
        private const int SCARD_S_SUCCESS = 0x00000000;

        private const int SCARD_SCOPE_USER = 0x0000;
        private const int SCARD_SHARE_SHARED = 0x0002;
        private const int SCARD_PROTOCOL_T0 = 0x0001;
        private const int SCARD_PROTOCOL_T1 = 0x0002;
        private const int SCARD_LEAVE_CARD = 0x0000;

        // Ошибки
        private const int SCARD_E_NO_SMARTCARD = unchecked((int)0x8010000C);

        private const int SCARD_E_SHARING_VIOLATION = unchecked((int)0x8010000F);

        [StructLayout(LayoutKind.Sequential)]
        private struct SCARD_IO_REQUEST
        {
            public int dwProtocol;
            public int cbPciLength;
        }

        private static SCARD_IO_REQUEST IOREQ_T0 = new SCARD_IO_REQUEST { dwProtocol = SCARD_PROTOCOL_T0, cbPciLength = 8 };
        private static SCARD_IO_REQUEST IOREQ_T1 = new SCARD_IO_REQUEST { dwProtocol = SCARD_PROTOCOL_T1, cbPciLength = 8 };

        [DllImport("winscard.dll")]
        private static extern int SCardEstablishContext(int dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

        [DllImport("winscard.dll")]
        private static extern int SCardReleaseContext(IntPtr hContext);

        [DllImport("winscard.dll")]
        private static extern int SCardListReaders(IntPtr hContext, byte[] mszGroups, byte[] mszReaders, ref int pcchReaders);

        [DllImport("winscard.dll", CharSet = CharSet.Auto)]
        private static extern int SCardConnect(IntPtr hContext, string szReader, int dwShareMode, int dwPreferredProtocols, out IntPtr phCard, out int pdwActiveProtocol);

        [DllImport("winscard.dll")]
        private static extern int SCardDisconnect(IntPtr hCard, int dwDisposition);

        [DllImport("winscard.dll")]
        private static extern int SCardTransmit(IntPtr hCard, ref SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, int cbSendLength, IntPtr pioRecvPci, byte[] pbRecvBuffer, ref int pcbRecvLength);
    }
}
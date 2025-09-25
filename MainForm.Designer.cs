using System;
using System.Windows.Forms;
using System.Drawing;

namespace LibraryTerminal
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            // --------- Панели ---------
            this.panelMenu = new System.Windows.Forms.Panel();
            this.panelWaitCardTake = new System.Windows.Forms.Panel();
            this.panelWaitCardReturn = new System.Windows.Forms.Panel();
            this.panelScanBook = new System.Windows.Forms.Panel();
            this.panelScanBookReturn = new System.Windows.Forms.Panel();
            this.panelSuccess = new System.Windows.Forms.Panel();
            this.panelError = new System.Windows.Forms.Panel();
            this.panelOverflow = new System.Windows.Forms.Panel();
            this.panelNoTag = new System.Windows.Forms.Panel();

            // --------- Кнопки меню ---------
            this.btnTakeBook = new System.Windows.Forms.Button();
            this.btnReturnBook = new System.Windows.Forms.Button();

            // --------- Подписи (экраны) ---------
            this.lblTitleMenu = new System.Windows.Forms.Label();
            this.lblWaitCardTake = new System.Windows.Forms.Label();
            this.lblWaitCardReturn = new System.Windows.Forms.Label();
            this.lblScanBook = new System.Windows.Forms.Label();
            this.lblScanBookReturn = new System.Windows.Forms.Label();
            this.lblSuccess = new System.Windows.Forms.Label();
            this.lblOverflow = new System.Windows.Forms.Label();
            this.lblNoTag = new System.Windows.Forms.Label();
            this.lblError = new System.Windows.Forms.Label();

            // --------- НОВОЕ: инфо о читателе ---------
            this.lblReaderInfoTake = new System.Windows.Forms.Label();
            this.lblReaderInfoReturn = new System.Windows.Forms.Label();

            // --------- поля под демо-кнопки (не используются дизайнером) ---------
            this.btnSimCardTake = new System.Windows.Forms.Button();
            this.btnSimCardReturn = new System.Windows.Forms.Button();
            this.btnSimBookTake = new System.Windows.Forms.Button();
            this.btnSimBookReturn = new System.Windows.Forms.Button();

            this.SuspendLayout();

            // ========= Форма =========
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            this.ClientSize = new System.Drawing.Size(800, 600);
            this.Name = "MainForm";
            this.Text = "Library Terminal";
            this.KeyPreview = true;
            this.Load += new System.EventHandler(this.MainForm_Load);

            // ========= Панель меню =========
            this.panelMenu.Location = new Point(0, 0);
            this.panelMenu.Size = new Size(800, 600);
            this.panelMenu.BackColor = Color.White;

            // Заголовок
            this.lblTitleMenu.AutoSize = false;
            this.lblTitleMenu.TextAlign = ContentAlignment.MiddleCenter;
            this.lblTitleMenu.Font = new Font("Segoe UI", 22F, FontStyle.Bold);
            this.lblTitleMenu.Text = "Библиотека\nФилиал №1";
            this.lblTitleMenu.Dock = DockStyle.Top;
            this.lblTitleMenu.Height = 160;

            // Кнопка «Взять книгу»
            this.btnTakeBook.Size = new Size(320, 80);
            this.btnTakeBook.Location = new Point((800 - 320) / 2, 200);
            this.btnTakeBook.Text = "📕 Взять книгу";
            this.btnTakeBook.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
            this.btnTakeBook.Click += new EventHandler(this.btnTakeBook_Click);

            // Кнопка «Вернуть книгу»
            this.btnReturnBook.Size = new Size(320, 80);
            this.btnReturnBook.Location = new Point((800 - 320) / 2, 300);
            this.btnReturnBook.Text = "📗 Вернуть книгу";
            this.btnReturnBook.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
            this.btnReturnBook.Click += new EventHandler(this.btnReturnBook_Click);

            this.panelMenu.Controls.Add(this.btnTakeBook);
            this.panelMenu.Controls.Add(this.btnReturnBook);
            this.panelMenu.Controls.Add(this.lblTitleMenu);

            // ========= Остальные экраны =========

            // S2 — Ожидание карты (выдача)
            this.panelWaitCardTake.Location = new Point(0, 0);
            this.panelWaitCardTake.Size = new Size(800, 600);
            SetupBigLabel(this.lblWaitCardTake, "Приложите карту читателя для выдачи");
            this.panelWaitCardTake.Controls.Add(this.lblWaitCardTake);
            AddMarqueeTo(this.panelWaitCardTake);

            // S4 — Ожидание карты (возврат)
            this.panelWaitCardReturn.Location = new Point(0, 0);
            this.panelWaitCardReturn.Size = new Size(800, 600);
            SetupBigLabel(this.lblWaitCardReturn, "Приложите карту читателя для возврата");
            this.panelWaitCardReturn.Controls.Add(this.lblWaitCardReturn);
            AddMarqueeTo(this.panelWaitCardReturn);

            // ====== S3 — Ожидание книги (выдача) ======
            this.panelScanBook.Location = new Point(0, 0);
            this.panelScanBook.Size = new Size(800, 600);

            // линия с brief
            this.lblReaderInfoTake.AutoSize = false;
            this.lblReaderInfoTake.Dock = DockStyle.Top;
            this.lblReaderInfoTake.Height = 72;
            this.lblReaderInfoTake.TextAlign = ContentAlignment.MiddleCenter;
            this.lblReaderInfoTake.Font = new Font("Segoe UI", 14F, FontStyle.Regular);
            this.lblReaderInfoTake.ForeColor = Color.FromArgb(45, 45, 45);
            this.lblReaderInfoTake.Text = "";

            // основной текст
            SetupBigLabel(this.lblScanBook, "Поднесите книгу к считывателю");

            this.panelScanBook.Controls.Add(this.lblScanBook);
            this.panelScanBook.Controls.Add(this.lblReaderInfoTake);
            AddMarqueeTo(this.panelScanBook);

            // ====== S5 — Ожидание книги (возврат) ======
            this.panelScanBookReturn.Location = new Point(0, 0);
            this.panelScanBookReturn.Size = new Size(800, 600);

            this.lblReaderInfoReturn.AutoSize = false;
            this.lblReaderInfoReturn.Dock = DockStyle.Top;
            this.lblReaderInfoReturn.Height = 72;
            this.lblReaderInfoReturn.TextAlign = ContentAlignment.MiddleCenter;
            this.lblReaderInfoReturn.Font = new Font("Segoe UI", 14F, FontStyle.Regular);
            this.lblReaderInfoReturn.ForeColor = Color.FromArgb(45, 45, 45);
            this.lblReaderInfoReturn.Text = "";

            SetupBigLabel(this.lblScanBookReturn, "Поднесите возвращаемую книгу к считывателю");

            this.panelScanBookReturn.Controls.Add(this.lblScanBookReturn);
            this.panelScanBookReturn.Controls.Add(this.lblReaderInfoReturn);
            AddMarqueeTo(this.panelScanBookReturn);

            // S6 — Успех
            this.panelSuccess.Location = new Point(0, 0);
            this.panelSuccess.Size = new Size(800, 600);
            SetupBigLabel(this.lblSuccess, "Спасибо!\nОперация выполнена");
            this.panelSuccess.Controls.Add(this.lblSuccess);

            // S9 — Нет места
            this.panelOverflow.Location = new Point(0, 0);
            this.panelOverflow.Size = new Size(800, 600);
            SetupBigLabel(this.lblOverflow, "Нет места\nОбратитесь, пожалуйста, в библиотеку");
            this.panelOverflow.Controls.Add(this.lblOverflow);

            // S7 — Книга не принята
            this.panelNoTag.Location = new Point(0, 0);
            this.panelNoTag.Size = new Size(800, 600);
            SetupBigLabel(this.lblNoTag, "Книга не принята\nЗаберите книгу и обратитесь в библиотеку");
            this.panelNoTag.Controls.Add(this.lblNoTag);

            // S8 — Ошибка карты/авторизации
            this.panelError.Location = new Point(0, 0);
            this.panelError.Size = new Size(800, 600);
            this.lblError.AutoSize = false;
            this.lblError.Dock = DockStyle.Fill;
            this.lblError.TextAlign = ContentAlignment.MiddleCenter;
            this.lblError.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
            this.lblError.Text = "Ошибка";
            this.panelError.Controls.Add(this.lblError);

            // ========= Добавление всех панелей =========
            this.Controls.Add(this.panelMenu);
            this.Controls.Add(this.panelWaitCardTake);
            this.Controls.Add(this.panelWaitCardReturn);
            this.Controls.Add(this.panelScanBook);
            this.Controls.Add(this.panelScanBookReturn);
            this.Controls.Add(this.panelSuccess);
            this.Controls.Add(this.panelError);
            this.Controls.Add(this.panelOverflow);
            this.Controls.Add(this.panelNoTag);

            // ========= Видимость на старте =========
            this.panelMenu.Visible = true;
            this.panelWaitCardTake.Visible = false;
            this.panelWaitCardReturn.Visible = false;
            this.panelScanBook.Visible = false;
            this.panelScanBookReturn.Visible = false;
            this.panelSuccess.Visible = false;
            this.panelError.Visible = false;
            this.panelOverflow.Visible = false;
            this.panelNoTag.Visible = false;

            this.ResumeLayout(false);

            // Кнопка «Тест ИРБИС» на панели меню
            var btnTestIrbis = new System.Windows.Forms.Button();
            btnTestIrbis.Size = new System.Drawing.Size(200, 44);
            btnTestIrbis.Location = new System.Drawing.Point((800 - 200) / 2, 410);
            btnTestIrbis.Text = "Тест ИРБИС (F9)";
            btnTestIrbis.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular);
            btnTestIrbis.Click += this.TestIrbisConnection;
            this.panelMenu.Controls.Add(btnTestIrbis);
        }

        // Хелперы для оформления экранов
        private void SetupBigLabel(Label lbl, string text)
        {
            lbl.AutoSize = false;
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.Font = new Font("Segoe UI", 28F, FontStyle.Bold);
            lbl.Text = text;
            lbl.Dock = DockStyle.Fill;
        }

        private void AddMarqueeTo(Panel p)
        {
            var pr = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                Dock = DockStyle.Bottom,
                Height = 12,
                MarqueeAnimationSpeed = 35
            };
            p.Controls.Add(pr);
            pr.BringToFront();
        }

        // -------- Поля (Designer) --------
        private Panel panelMenu;
        private Panel panelWaitCardTake;
        private Panel panelWaitCardReturn;
        private Panel panelScanBook;
        private Panel panelScanBookReturn;
        private Panel panelSuccess;
        private Panel panelError;
        private Panel panelOverflow;
        private Panel panelNoTag;

        private Button btnTakeBook;
        private Button btnReturnBook;

        private Label lblTitleMenu;
        private Label lblWaitCardTake;
        private Label lblWaitCardReturn;
        private Label lblScanBook;
        private Label lblScanBookReturn;
        private Label lblSuccess;
        private Label lblOverflow;
        private Label lblNoTag;
        private Label lblError;

        // НОВОЕ: инфоблок о читателе
        private Label lblReaderInfoTake;
        private Label lblReaderInfoReturn;

        // поля под демо-кнопки (создаются программно)
        private Button btnSimCardTake;
        private Button btnSimCardReturn;
        private Button btnSimBookTake;
        private Button btnSimBookReturn;
    }
}

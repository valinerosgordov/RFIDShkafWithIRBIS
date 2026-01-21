using System;
using System.Collections.Generic;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace LibraryTerminal
{
    /// <summary>
    /// Управление экранами приложения
    /// </summary>
    public class ScreenManager
    {
        private readonly Dictionary<Screen, Panel> _screens;
        private Screen _currentScreen;
        private readonly Timer _timeoutTimer;
        private DateTime? _timeoutDeadline;
        private readonly int _defaultTimeoutSeconds;

        public ScreenManager(Control parentForm, int defaultTimeoutSeconds = 10)
        {
            _screens = new Dictionary<Screen, Panel>();
            _defaultTimeoutSeconds = defaultTimeoutSeconds;
            _timeoutTimer = new Timer { Interval = 250 };
            _timeoutTimer.Tick += TimeoutTimer_Tick;
        }

        public Screen CurrentScreen => _currentScreen;

        public event EventHandler<Screen> ScreenChanged;
        public event EventHandler TimeoutReached;

        /// <summary>
        /// Зарегистрировать экран
        /// </summary>
        public void RegisterScreen(Screen screen, Panel panel)
        {
            _screens[screen] = panel;
            panel.Visible = false;
            panel.Dock = DockStyle.Fill;
        }

        /// <summary>
        /// Перейти на указанный экран
        /// </summary>
        public void NavigateTo(Screen screen, int? timeoutSeconds = null)
        {
            if (!_screens.ContainsKey(screen))
                throw new ArgumentException($"Screen {screen} is not registered", nameof(screen));

            HideAllScreens();
            _screens[screen].Visible = true;
            _screens[screen].BringToFront();
            _currentScreen = screen;

            if (timeoutSeconds.HasValue)
            {
                _timeoutDeadline = DateTime.Now.AddSeconds(timeoutSeconds.Value);
                _timeoutTimer.Enabled = true;
            }
            else
            {
                _timeoutDeadline = null;
                _timeoutTimer.Enabled = false;
            }

            OnScreenChanged(screen);
        }

        /// <summary>
        /// Остановить таймер таймаута
        /// </summary>
        public void StopTimeout()
        {
            _timeoutDeadline = null;
            _timeoutTimer.Enabled = false;
        }

        private void HideAllScreens()
        {
            foreach (var panel in _screens.Values)
            {
                panel.Visible = false;
            }
        }

        private void TimeoutTimer_Tick(object sender, EventArgs e)
        {
            if (_timeoutDeadline.HasValue && DateTime.Now >= _timeoutDeadline.Value)
            {
                _timeoutDeadline = null;
                _timeoutTimer.Enabled = false;
                OnTimeoutReached();
            }
        }

        protected virtual void OnScreenChanged(Screen screen)
        {
            ScreenChanged?.Invoke(this, screen);
        }

        protected virtual void OnTimeoutReached()
        {
            TimeoutReached?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _timeoutTimer?.Stop();
            _timeoutTimer?.Dispose();
        }
    }

    /// <summary>
    /// Экраны приложения
    /// </summary>
    public enum Screen
    {
        MainMenu,
        WaitingCardForTake,
        WaitingBookForTake,
        WaitingCardForReturn,
        WaitingBookForReturn,
        Success,
        BookRejected,
        CardValidationFailed,
        NoSpaceAvailable
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Numerics;
using Telegram.Td.Api;
using Unigram.Collections;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Navigation;
using Unigram.Navigation.Services;
using Unigram.Services;
using Unigram.ViewModels;
using Unigram.Views.SignIn;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Point = Windows.Foundation.Point;

namespace Unigram.Views.Host
{
    public sealed partial class RootPage : Page
    {
        private ILifetimeService _lifetime;
        private NavigationService _navigationService;

        private RootDestination _navigationViewSelected;
        private MvxObservableCollection<object> _navigationViewItems;
        private readonly Dictionary<int, User> _sessionUsers = new Dictionary<int, User>();
        private readonly HashSet<int> _pendingSessionUsers = new HashSet<int>();
        private int _accountSelectorDiagnosticBudget = 24;
        private long _accountSelectorSessionsHash = long.MinValue;
        private bool _accountSelectorWasExpanded;

        public RootPage(NavigationService service)
        {
            if (SettingsService.Current.Appearance.RequestedTheme != ElementTheme.Default)
            {
                RequestedTheme = SettingsService.Current.Appearance.GetCalculatedElementTheme();
            }

            InitializeComponent();

            _lifetime = TLContainer.Current.Lifetime;
            _lifetime.Items.CollectionChanged += OnSessionsChanged;

            _navigationViewSelected = RootDestination.Chats;
            _navigationViewItems = new MvxObservableCollection<object>
            {
                //RootDestination.Separator,

                RootDestination.NewChat,
                RootDestination.NewSecretChat,
                RootDestination.NewChannel,

                RootDestination.Separator,

                RootDestination.Chats,
                RootDestination.Contacts,
                RootDestination.Calls,
                RootDestination.Settings,

                //RootDestination.Separator,

                //RootDestination.Wallet,

                RootDestination.Separator,

                RootDestination.SavedMessages,
                RootDestination.News
            };

            NavigationViewList.ItemsSource = _navigationViewItems;

            service.Frame.Navigating += OnNavigating;
            service.Frame.Navigated += OnNavigated;
            _navigationService = service;

            InitializeTitleBar();
            InitializeNavigation(service.Frame);
            InitializeLocalization();

            Navigation.Content = _navigationService.Frame;

            var shadow = DropShadowEx.Attach(ThemeShadow, 20, 0.25f);
            ThemeShadow.SizeChanged += (s, args) =>
            {
                shadow.Size = args.NewSize.ToVector2();
            };
        }

        public void UpdateComponent()
        {
            _contentLoaded = false;
            Resources.Clear();
            InitializeComponent();

            _navigationViewSelected = RootDestination.Chats;
            NavigationViewList.ItemsSource = _navigationViewItems;

            InitializeTitleBar();
            InitializeNavigation(_navigationService.Frame);
            InitializeLocalization();

            Switch(_lifetime.ActiveItem);
        }

        private void InitializeTitleBar()
        {
            if (ApiInfo.IsFullExperience &&
                UIViewSettings.GetForCurrentView().UserInteractionMode == UserInteractionMode.Mouse) return;
            var sender = CoreApplication.GetCurrentView().TitleBar;
            Navigation.Padding = new Thickness(0, sender.IsVisible ? sender.Height : 0, 0, 0);
        }

        public void SetTopPadding(Thickness thickness)
        {
            Navigation.SetTopPadding(thickness);
        }

        public void Create()
        {
            Switch(_lifetime.Create());
        }

        public void Switch(ISessionService session)
        {
            _lifetime.ActiveItem = session;

            if (_navigationService != null)
            {
                Destroy(_navigationService);
            }

            Navigation.IsPaneOpen = false;
            Navigation.SetTopPadding(new Thickness());

            var service = WindowContext.GetForCurrentView().NavigationServices.GetByFrameId($"{session.Id}") as NavigationService;
            if (service == null)
            {
                service = BootStrapper.Current.NavigationServiceFactory(BootStrapper.BackButton.Attach, BootStrapper.ExistingContent.Exclude, new Frame(), session.Id, $"{session.Id}", true) as NavigationService;
                service.Frame.Navigating += OnNavigating;
                service.Frame.Navigated += OnNavigated;

                switch (session.ProtoService.GetAuthorizationState())
                {
                    case AuthorizationStateReady ready:
                        service.Navigate(typeof(MainPage));
                        break;
                    case AuthorizationStateWaitPhoneNumber waitPhoneNumber:
                    case AuthorizationStateWaitOtherDeviceConfirmation waitOtherDeviceConfirmation:
                        service.Navigate(typeof(SignInPage));
                        service.Frame.BackStack.Add(new PageStackEntry(typeof(BlankPage), null, null));
                        break;
                    case AuthorizationStateWaitCode waitCode:
                        service.Navigate(typeof(SignInSentCodePage));
                        break;
                    case AuthorizationStateWaitRegistration waitRegistration:
                        service.Navigate(typeof(SignUpPage));
                        break;
                    case AuthorizationStateWaitPassword waitPassword:
                        service.Navigate(typeof(SignInPasswordPage));
                        break;
#if MODERN_TDLIB
                    case null:
                    case AuthorizationStateWaitTdlibParameters waitTdlibParameters:
                        // TDLib can still be starting when the shell is created. Keep the
                        // content frame usable until the first authorization update arrives.
                        service.Navigate(typeof(IntroPage));
                        break;
#endif
                }

                //WindowContext.GetForCurrentView().Handle(session, new UpdateConnectionState(session.ProtoService.GetConnectionState()));
                session.Aggregator.Publish(new UpdateConnectionState(session.ProtoService.GetConnectionState()));
            }
            else
            {
                // TODO: This should actually __never__ happen.
            }

            _navigationService = service;
            Navigation.Content = service.Frame;
        }

        private void Destroy(NavigationService master)
        {
            if (master.Frame.Content is IRootContentPage content)
            {
                content.Root = null;
                content.Dispose();
            }

            var detail = WindowContext.GetForCurrentView().NavigationServices.GetByFrameId($"Main{master.FrameFacade.FrameId}");
            if (detail != null)
            {
                detail.Navigate(typeof(BlankPage));
                detail.ClearCache();
            }

            master.Frame.Navigating -= OnNavigating;
            master.Frame.Navigated -= OnNavigated;
            master.Frame.Navigate(typeof(BlankPage));

            WindowContext.GetForCurrentView().NavigationServices.Remove(master);
            WindowContext.GetForCurrentView().NavigationServices.Remove(detail);
        }

        private void OnNavigating(object sender, NavigatingCancelEventArgs e)
        {
            if (_navigationService?.Frame?.Content is IRootContentPage content)
            {
                content.Root = null;
            }
        }

        private void OnNavigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is IRootContentPage content)
            {
                content.Root = this;
                Navigation.PaneToggleButtonVisibility = content.EvalutatePaneToggleButtonVisibility();
                InitializeNavigation(sender as Frame);
            }
            else
            {
                Navigation.PaneToggleButtonVisibility = Visibility.Collapsed;
            }
        }

        private void InitializeLocalization()
        {
            //NavigationChats.Text = Strings.Additional.Chats;
            //NavigationAbout.Content = Strings.Additional.About;
            //NavigationNews.Text = Strings.Additional.News;
        }

        private void InitializeNavigation(Frame frame)
        {
            if (frame?.Content is MainPage main)
            {
                InitializeUser(main.ViewModel);
            }

            InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
        }

        private async void OnSessionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (Dispatcher.HasThreadAccess)
            {
                InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
            });
        }

        private async void InitializeUser(MainViewModel viewModel)
        {
            var user = viewModel.CacheService.GetUser(viewModel.CacheService.Options.MyId);
            if (user == null)
            {
                user = await viewModel.ProtoService.SendAsync(new GetMe()) as User;
            }

            if (user == null)
            {
                return;
            }

            NameLabel.Text = user.GetFullName();
#if DEBUG
            PhoneLabel.Text = "+42 --- --- ----";
#else
            if (viewModel.Chats.Settings.UseTestDC)
            {
                PhoneLabel.Text = "+42 --- --- ----";
            }
            else
            {
                PhoneLabel.Text = PhoneNumber.Format(user.PhoneNumber);
            }
#endif
            Expanded.IsChecked = SettingsService.Current.IsAccountsSelectorExpanded;
            Automation.SetToolTip(Accounts, SettingsService.Current.IsAccountsSelectorExpanded ? Strings.Resources.AccDescrHideAccounts : Strings.Resources.AccDescrShowAccounts);
        }

        private static IList<ISessionService> GetSessionsSnapshot()
        {
            return TLContainer.Current.GetSessions().OrderBy(session => session.Id).ToList();
        }

        private static long GetSessionsHash(IList<ISessionService> sessions)
        {
            unchecked
            {
                var hash = 17L;
                foreach (var session in sessions)
                {
                    hash = hash * 31 + session.Id;
                }

                return hash;
            }
        }

        private User GetSessionUser(ISessionService session)
        {
            var user = session.ProtoService.GetUser(session.UserId);
            if (user == null)
            {
                _sessionUsers.TryGetValue(session.Id, out user);
            }

            return user;
        }

        private void RefreshSessionContainers(IList<ISessionService> sessions)
        {
            foreach (var session in sessions)
            {
                var container = NavigationViewList.ContainerFromItem(session) as ListViewItem;
                var content = container?.ContentTemplateRoot as Grid;
                if (content == null)
                {
                    continue;
                }

                var user = GetSessionUser(session);
                if (user == null)
                {
                    RequestSessionUser(session);
                    continue;
                }

                var title = content.Children[2] as TextBlock;
                title.Text = user.GetFullName();
                Automation.SetToolTip(content, title.Text);

                var photo = content.Children[0] as ProfilePicture;
                photo.Source = PlaceholderHelper.GetUser(session.ProtoService, user, 28);
            }
        }

        private void RequestSessionUser(ISessionService session)
        {
            if (session.UserId == 0 || !_pendingSessionUsers.Add(session.Id))
            {
                TraceAccountSelector($"stage=request;slot={session.Id};queued=0");
                return;
            }

            TraceAccountSelector($"stage=request;slot={session.Id};queued=1");
            session.ProtoService.Send(new GetUser(session.UserId), response =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _pendingSessionUsers.Remove(session.Id);

                    if (response is User user && user.Id == session.UserId)
                    {
                        _sessionUsers[session.Id] = user;
                        TraceAccountSelector($"stage=response;slot={session.Id};result=user");
                        InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
                    }
                    else
                    {
                        TraceAccountSelector($"stage=response;slot={session.Id};result={response?.GetType().Name ?? "null"}");
                    }
                });
            });
        }

        private void TraceAccountSelector(string value)
        {
            if (System.Threading.Interlocked.Decrement(ref _accountSelectorDiagnosticBudget) >= 0)
            {
                Logs.PushDiagnostics.Write("account_selector", value);
            }
        }

        private void InitializeSessions(bool show, IList<ISessionService> items)
        {
            var sessionsHash = GetSessionsHash(items);
            if (_accountSelectorSessionsHash == sessionsHash && _accountSelectorWasExpanded == show)
            {
                RefreshSessionContainers(items);
                TraceAccountSelector($"stage=refresh;expanded={(show ? 1 : 0)};source={items.Count};bound={NavigationViewList.Items.Count}");
                return;
            }

            var navigationItems = _navigationViewItems.ToList();

            for (int i = 0; i < navigationItems.Count; i++)
            {
                if (navigationItems[i] is ISessionService)
                {
                    navigationItems.RemoveAt(i);
                    i--;
                }
                else if (navigationItems[i] is RootDestination viewItem && viewItem == RootDestination.AddAccount)
                {
                    navigationItems.RemoveAt(i);

                    if (i < navigationItems.Count && navigationItems[i] is RootDestination destination && destination == RootDestination.Separator)
                    {
                        navigationItems.RemoveAt(i);
                        break;
                    }
                }
            }

            if (show && items != null)
            {
                navigationItems.Insert(0, RootDestination.Separator);
                navigationItems.Insert(0, RootDestination.AddAccount);

                for (int k = items.Count - 1; k >= 0; k--)
                {
                    navigationItems.Insert(0, items[k]);
                }
            }

            _navigationViewItems.ReplaceWith(navigationItems);
            _accountSelectorSessionsHash = sessionsHash;
            _accountSelectorWasExpanded = show;
            TraceAccountSelector($"stage=rebuild;expanded={(show ? 1 : 0)};source={items?.Count ?? 0};rows={navigationItems.OfType<ISessionService>().Count()};bound={NavigationViewList.Items.Count}");
        }

        #region Recycling

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.Item is ISessionService && (args.ItemContainer == null || args.ItemContainer is Controls.NavigationViewItem || args.ItemContainer is Controls.NavigationViewItemSeparator))
            {
                args.ItemContainer = new ListViewItem();
                args.ItemContainer.Style = NavigationViewList.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = Resources["SessionItemTemplate"] as DataTemplate;
                args.ItemContainer.ContextRequested += OnContextRequested;
            }
            else if (args.Item is RootDestination destination)
            {
                if (destination == RootDestination.Separator && !(args.ItemContainer is Controls.NavigationViewItemSeparator))
                {
                    args.ItemContainer = new Controls.NavigationViewItemSeparator();
                }
                else if (destination != RootDestination.Separator && !(args.ItemContainer is Controls.NavigationViewItem))
                {
                    args.ItemContainer = new Controls.NavigationViewItem();
                    args.ItemContainer.ContextRequested += OnContextRequested;
                }
            }

            args.IsContainerPrepared = true;
        }

        private void OnContextRequested(UIElement sender, Windows.UI.Xaml.Input.ContextRequestedEventArgs args)
        {
            var container = sender as Controls.NavigationViewItem;
            if (container is ISessionService session && !session.IsActive)
            {

            }
            else if (container.Content is RootDestination destination && destination == RootDestination.AddAccount)
            {
                var alt = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
                var ctrl = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                var shift = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

                if (alt && !ctrl && shift)
                {
                    var flyout = new MenuFlyout();

                    flyout.CreateFlyoutItem(new RelayCommand(() => Switch(_lifetime.Create(test: false))), "Production Server", new FontIcon { Glyph = "\uE774" });
                    flyout.CreateFlyoutItem(new RelayCommand(() => Switch(_lifetime.Create(test: true))), "Test Server", new FontIcon { Glyph = "\uE825" });

                    args.ShowAt(flyout, container);
                }
            }
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.Item is ISessionService session)
            {
                var content = args.ItemContainer.ContentTemplateRoot as Grid;
                if (content == null)
                {
                    return;
                }

                var user = GetSessionUser(session);

                if (user == null)
                {
                    RequestSessionUser(session);
                }

                TraceAccountSelector($"stage=container;slot={session.Id};phase={args.Phase};profile={(user != null ? 1 : 0)}");

                if (args.Phase == 0)
                {
                    var title = content.Children[2] as TextBlock;
                    title.Text = user?.GetFullName() ?? Strings.Resources.Loading;

                    Automation.SetToolTip(content, title.Text);
                }
                else if (args.Phase == 2 && user != null)
                {
                    var photo = content.Children[0] as ProfilePicture;
                    photo.Source = PlaceholderHelper.GetUser(session.ProtoService, user, 28);
                }

                if (args.Phase < 2)
                {
                    args.RegisterUpdateCallback(OnContainerContentChanging);
                }
            }
            else if (args.Item is RootDestination destination)
            {
                var content = args.ItemContainer as Controls.NavigationViewItem;
                if (content != null)
                {
                    content.IsChecked = destination == _navigationViewSelected;

                    if (destination == RootDestination.SavedMessages)
                    {
                        content.FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Telegram.ttf#Telegram");
                    }
                    else
                    {
                        content.FontFamily = new FontFamily("Segoe MDL2 Assets");
                    }
                }

                switch (destination)
                {
                    case RootDestination.AddAccount:
                        content.Text = Strings.Resources.AddAccount;
                        content.Glyph = "\uE109";
                        break;

                    case RootDestination.NewChat:
                        content.Text = Strings.Resources.NewGroup;
                        content.Glyph = "\uE902";
                        break;
                    case RootDestination.NewSecretChat:
                        content.Text = Strings.Resources.NewSecretChat;
                        content.Glyph = "\uE1F6";
                        break;
                    case RootDestination.NewChannel:
                        content.Text = Strings.Resources.NewChannel;
                        content.Glyph = "\uE789";
                        break;

                    case RootDestination.Chats:
                        content.Text = Strings.Resources.FilterChats;
                        content.Glyph = "\uE8BD";
                        break;
                    case RootDestination.Contacts:
                        content.Text = Strings.Resources.Contacts;
                        content.Glyph = "\uE716";
                        break;
                    case RootDestination.Calls:
                        content.Text = Strings.Resources.Calls;
                        content.Glyph = "\uE717";
                        break;
                    case RootDestination.Settings:
                        content.Text = Strings.Resources.Settings;
                        content.Glyph = "\uE115";
                        break;

                    case RootDestination.SavedMessages:
                        content.Text = Strings.Resources.SavedMessages;
                        content.Glyph = "\uE907";
                        break;

                    case RootDestination.News:
                        content.Text = Strings.Additional.News;
                        content.Glyph = "\uE789";
                        break;
                }
            }
        }

        #endregion

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Current.IsAccountsSelectorExpanded = !SettingsService.Current.IsAccountsSelectorExpanded;

            InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
            Expanded.IsChecked = SettingsService.Current.IsAccountsSelectorExpanded;

            Automation.SetToolTip(Accounts, SettingsService.Current.IsAccountsSelectorExpanded ? Strings.Resources.AccDescrHideAccounts : Strings.Resources.AccDescrShowAccounts);
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ISessionService session)
            {
                if (session.IsActive)
                {
                    return;
                }

                Switch(session);
            }
            else if (e.ClickedItem is RootDestination destination)
            {
                if (destination == RootDestination.AddAccount)
                {
                    Switch(_lifetime.Create());
                }
                else if (_navigationService?.Frame?.Content is IRootContentPage content)
                {
                    content.NavigationView_ItemClick(destination);
                }
            }

            Navigation.IsPaneOpen = false;

            var scroll = NavigationViewList.GetScrollViewer();
            if (scroll != null)
            {
                scroll.ChangeView(null, 0, null, true);
            }
        }

        #region Exposed

        public void SetPaneToggleButtonVisibility(Visibility value)
        {
            Navigation.PaneToggleButtonVisibility = value;
        }

        public void SetSelectedIndex(RootDestination value)
        {
            _navigationViewSelected = value;

            void SetChecked(RootDestination destination, RootDestination target)
            {
                var selector = NavigationViewList.ContainerFromItem(_navigationViewItems.FirstOrDefault(x => x is RootDestination y && y == destination)) as Controls.NavigationViewItem;
                if (selector != null)
                {
                    selector.IsChecked = destination == target;
                }
            }

            SetChecked(RootDestination.Chats, value);
            SetChecked(RootDestination.Contacts, value);
            SetChecked(RootDestination.Calls, value);
            SetChecked(RootDestination.Settings, value);
        }

        #endregion

        public void ShowEditor(ThemeCustomInfo theme)
        {
            var resize = ThemePage == null;

            FindName("ThemePage");
            ThemePage.Load(theme);

            if (resize)
            {
                MainColumn.Width = new GridLength(ActualWidth, GridUnitType.Pixel);

                var view = ApplicationView.GetForCurrentView();
                var size = view.VisibleBounds;

                view.TryResizeView(new Size(size.Width + 320, size.Height));
                ApplicationView.PreferredLaunchViewSize = new Size(size.Width, size.Height);

                MainColumn.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        public void HideEditor()
        {
            UnloadObject(ThemePage);

            var view = ApplicationView.GetForCurrentView();
            view.TryResizeView(ApplicationView.PreferredLaunchViewSize);
        }
    }

    public interface IRootContentPage
    {
        RootPage Root { get; set; }

        void NavigationView_ItemClick(RootDestination destination);

        //Visibility PaneToggleButtonVisibility { get; }

        Visibility EvalutatePaneToggleButtonVisibility();

        void Dispose();
    }

    public enum RootDestination
    {
        AddAccount,

        NewChat,
        NewSecretChat,
        NewChannel,

        Chats,
        Contacts,
        Calls,
        Settings,

        InviteFriends,

        SavedMessages,
        News,

        Separator
    }
}

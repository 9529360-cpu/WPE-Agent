using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人;

public partial class LoginWindow:Window
{
    private readonly LocalAccountService _accounts=new();private string _mode="login";private static LocalizationService I18n=>LocalizationService.Current;public string AuthenticatedUser{get;private set;}=string.Empty;
    public LoginWindow(){InitializeComponent();Loaded+=(_,_)=>{CreateModeButton.Visibility=_accounts.CanCreateInitialAccount?Visibility.Visible:Visibility.Collapsed;var remembered=_accounts.TryRememberedLogin();if(remembered.Success){AuthenticatedUser=remembered.UserName;DialogResult=true;}};I18n.LanguageChanged+=LanguageChanged;}
    protected override void OnClosed(EventArgs e){I18n.LanguageChanged-=LanguageChanged;base.OnClosed(e);}
    private void LanguageChanged()=>Dispatcher.Invoke(()=>Language=System.Windows.Markup.XmlLanguage.GetLanguage(I18n.Culture.IetfLanguageTag));
    private void Mode_Click(object sender,RoutedEventArgs e){var requested=(sender as Button)?.Tag?.ToString()??"login";_mode=requested=="create"&& !_accounts.CanCreateInitialAccount?"login":requested;ExtraLabel.Visibility=ExtraBox.Visibility=_mode=="create"?Visibility.Visible:Visibility.Collapsed;RecoveryLabel.Visibility=RecoveryBox.Visibility=_mode=="reset"?Visibility.Visible:Visibility.Collapsed;RememberBox.Visibility=_mode=="login"?Visibility.Visible:Visibility.Collapsed;ModeTitle.Text=I18n.T(_mode=="create"?"Access.Create":_mode=="reset"?"Access.Reset":"Access.SignIn");PrimaryButton.Content=I18n.T(_mode=="create"?"Access.CreateButton":_mode=="reset"?"Access.ResetButton":"Access.SignInButton");StatusText.Text="";}
    private void Primary_Click(object sender,RoutedEventArgs e){try{if(_mode=="create"){if(PasswordBox.Password!=ExtraBox.Password)throw new InvalidOperationException(I18n.T("Access.PasswordMismatch"));var created=_accounts.Create(UserBox.Text,PasswordBox.Password);StatusText.Text=created.Result.Message;if(created.Result.Success){CreateModeButton.Visibility=Visibility.Collapsed;MessageBox.Show(I18n.T("Access.SaveRecovery",created.RecoveryCode),I18n.T("Access.RecoveryTitle"),MessageBoxButton.OK,MessageBoxImage.Information);_mode="login";Mode_Click(new Button{Tag="login"},new RoutedEventArgs());}}else if(_mode=="reset"){var reset=_accounts.ResetPassword(UserBox.Text,RecoveryBox.Text,PasswordBox.Password);StatusText.Text=reset.Message;if(reset.Success){_mode="login";Mode_Click(new Button{Tag="login"},new RoutedEventArgs());}}else{var result=_accounts.Login(UserBox.Text,PasswordBox.Password,RememberBox.IsChecked==true);StatusText.Text=result.Message;if(result.Success){AuthenticatedUser=result.UserName;DialogResult=true;}}}catch(Exception ex){StatusText.Text=ex.Message;}}
    private void Language_Click(object sender,RoutedEventArgs e){var menu=new ContextMenu{PlacementTarget=LanguageButton};foreach(var language in I18n.AvailableLanguages){var item=new MenuItem{Header=language.DisplayName,Tag=language.Code,IsCheckable=true,IsChecked=language.Code==I18n.CurrentCode};item.Click+=(_,_)=>I18n.SetLanguage((string)item.Tag);menu.Items.Add(item);}LanguageButton.ContextMenu=menu;menu.IsOpen=true;}
}

using System.Windows;
using System.Windows.Controls;
using 币安量化机器人.Services.Access;
using 币安量化机器人.Services.Localization;

namespace 币安量化机器人;

public partial class ActivationWindow:Window
{
    private readonly DeviceLicenseService _licenses;private static LocalizationService I18n=>LocalizationService.Current;public DeviceLicensePayload? ActivatedLicense{get;private set;}
    public ActivationWindow(DeviceLicenseService licenses){InitializeComponent();_licenses=licenses;DeviceCodeBox.Text=licenses.DeviceCode;I18n.LanguageChanged+=LanguageChanged;}
    protected override void OnClosed(EventArgs e){I18n.LanguageChanged-=LanguageChanged;base.OnClosed(e);}
    private void CopyDevice_Click(object sender,RoutedEventArgs e){Clipboard.SetText(DeviceCodeBox.Text);StatusText.Text=I18n.T("Activation.DeviceCopied");}
    private void Activate_Click(object sender,RoutedEventArgs e){var result=_licenses.Activate(ActivationCodeBox.Text);StatusText.Text=I18n.T(result.Message);if(result.Success&&result.License is not null){ActivatedLicense=result.License;DialogResult=true;}}
    private void Language_Click(object sender,RoutedEventArgs e){var menu=new ContextMenu{PlacementTarget=LanguageButton};foreach(var language in I18n.AvailableLanguages){var item=new MenuItem{Header=language.DisplayName,Tag=language.Code};item.Click+=(_,_)=>I18n.SetLanguage((string)item.Tag);menu.Items.Add(item);}LanguageButton.ContextMenu=menu;menu.IsOpen=true;}
    private void LanguageChanged()=>Dispatcher.Invoke(()=>Language=System.Windows.Markup.XmlLanguage.GetLanguage(I18n.Culture.IetfLanguageTag));
}

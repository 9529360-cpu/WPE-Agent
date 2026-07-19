namespace WpeLicenseGenerator;

internal sealed class LicenseGeneratorForm:Form
{
    private readonly TextBox _device=new(){Multiline=true,Height=68,ScrollBars=ScrollBars.Vertical};
    private readonly TextBox _licenseId=new(){Text=LicenseIssuer.CreateLicenseId()};
    private readonly ComboBox _validity=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly TextBox _result=new(){Multiline=true,Height=150,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
    private readonly Label _status=new(){AutoSize=true,ForeColor=Color.FromArgb(255,177,74)};

    public LicenseGeneratorForm()
    {
        Text="WPE Agent 激活码生成器 // 所有者专用";Width=820;Height=650;MinimumSize=new(720,580);StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(5,10,15);ForeColor=Color.FromArgb(215,235,243);Font=new Font("Microsoft YaHei UI",10);
        _validity.Items.AddRange(["永久授权","30 天","90 天","365 天"]);_validity.SelectedIndex=0;
        var title=new Label{Text="WPE // LICENSE AUTHORITY",Font=new Font(Font.FontFamily,20,FontStyle.Bold),AutoSize=true,ForeColor=Color.FromArgb(24,216,242)};
        var warning=new Label{Text="所有者专用：签名密钥受 Windows DPAPI 保护。不要把本工具和密钥交给客户。",AutoSize=true,ForeColor=Color.FromArgb(105,151,170)};
        var generate=Button("生成激活码",async(_,_)=>await GenerateAsync(),true);var copy=Button("复制激活码",(_,_)=>CopyResult());var save=Button("保存到文件",(_,_)=>SaveResult());
        var buttons=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.LeftToRight};buttons.Controls.AddRange([generate,copy,save]);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(34),ColumnCount=1,RowCount=12,AutoScroll=true};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.Controls.Add(title);layout.Controls.Add(warning);layout.Controls.Add(Label("客户设备码"));layout.Controls.Add(_device);layout.Controls.Add(Label("授权编号"));layout.Controls.Add(_licenseId);layout.Controls.Add(Label("有效期"));layout.Controls.Add(_validity);layout.Controls.Add(buttons);layout.Controls.Add(Label("生成结果"));layout.Controls.Add(_result);layout.Controls.Add(_status);Controls.Add(layout);
        foreach(var box in new[]{_device,_licenseId,_result})Style(box);Style(_validity);
    }

    private async Task GenerateAsync()
    {
        try
        {
            _status.Text="正在签发……";_result.Clear();var days=_validity.SelectedIndex switch{1=>30,2=>90,3=>365,_=>0};
            _result.Text=await LicenseIssuer.IssueAsync(_device.Text,_licenseId.Text,days);_status.ForeColor=Color.FromArgb(53,230,160);_status.Text=$"生成成功 · {_result.Text.Length} 字符 · {(days==0?"永久授权":days+" 天")}";
        }
        catch(Exception ex){_status.ForeColor=Color.FromArgb(255,110,110);_status.Text=ex.Message;}
    }
    private void CopyResult(){if(string.IsNullOrWhiteSpace(_result.Text)){_status.Text="请先生成激活码。";return;}Clipboard.SetText(_result.Text);_status.Text="激活码已复制。";}
    private void SaveResult()
    {
        if(string.IsNullOrWhiteSpace(_result.Text)){_status.Text="请先生成激活码。";return;}
        using var dialog=new SaveFileDialog{Filter="WPE activation (*.activation)|*.activation|Text (*.txt)|*.txt",FileName=$"{_licenseId.Text}.activation"};
        if(dialog.ShowDialog()!=DialogResult.OK)return;File.WriteAllText(dialog.FileName,_result.Text);_status.Text="已保存："+dialog.FileName;
    }
    private Button Button(string text,EventHandler click,bool primary=false){var button=new Button{Text=text,AutoSize=true,Padding=new Padding(16,8,16,8),FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(11,96,112):Color.FromArgb(16,39,51),ForeColor=Color.FromArgb(199,234,244)};button.FlatAppearance.BorderColor=primary?Color.FromArgb(24,216,242):Color.FromArgb(42,98,116);button.Click+=click;return button;}
    private static Label Label(string text)=>new(){Text=text,AutoSize=true,Margin=new Padding(0,12,0,4),ForeColor=Color.FromArgb(111,152,169)};
    private static void Style(Control control){control.Dock=DockStyle.Top;control.BackColor=Color.FromArgb(11,24,33);control.ForeColor=Color.FromArgb(215,235,243);control.Margin=new Padding(0,4,0,6);}
}

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class AboutBox
    Inherits Form

    Private ProductTitleLabel As Label
    Private VersionLabel As Label

    <Global.System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Dim layout As New FlowLayoutPanel()
        ProductTitleLabel = New Label()
        VersionLabel = New Label()
        Dim copyrightLabel As New Label()
        Dim contactLabel As New Label()
        Dim attributionLabel As New Label()
        Dim closeButton As New Button()

        SuspendLayout()
        layout.AutoSize = True
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink
        layout.Dock = DockStyle.Fill
        layout.FlowDirection = FlowDirection.TopDown
        layout.Padding = New Padding(24)
        layout.WrapContents = False

        ProductTitleLabel.AutoSize = True
        ProductTitleLabel.Font = New Drawing.Font(Font.FontFamily, 15.0F, Drawing.FontStyle.Bold)
        ProductTitleLabel.Margin = New Padding(0, 0, 0, 14)
        VersionLabel.AutoSize = True
        VersionLabel.Margin = New Padding(0, 0, 0, 14)

        copyrightLabel.AutoSize = True
        copyrightLabel.Text = "版权信息：北京华力创通科技股份有限公司"
        copyrightLabel.Margin = New Padding(0, 0, 0, 8)
        contactLabel.AutoSize = True
        contactLabel.Text = "联系方式：shimingsong@hwacreate.com.cn"
        contactLabel.Margin = New Padding(0, 0, 0, 16)
        attributionLabel.AutoSize = True
        attributionLabel.Text = "基于 Aquila Technology 的 WakeOnLAN，采用 GNU GPL v3 许可。"
        attributionLabel.Margin = New Padding(0, 0, 0, 16)

        closeButton.AutoSize = True
        closeButton.DialogResult = DialogResult.OK
        closeButton.Text = "确定"

        layout.Controls.AddRange({ProductTitleLabel, VersionLabel, copyrightLabel, contactLabel, attributionLabel, closeButton})
        AutoScaleMode = AutoScaleMode.Font
        AutoSize = True
        AutoSizeMode = AutoSizeMode.GrowAndShrink
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = False
        StartPosition = FormStartPosition.CenterParent
        Text = "关于"
        AcceptButton = closeButton
        CancelButton = closeButton
        Controls.Add(layout)
        ResumeLayout(True)
    End Sub
End Class

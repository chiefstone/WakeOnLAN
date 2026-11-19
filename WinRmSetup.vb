Imports System.Drawing
Imports System.Net
Imports System.Net.Security
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Security.Cryptography.X509Certificates
Imports System.Text.RegularExpressions

Friend NotInheritable Class WinRmSetup
    Inherits Form

    Private ReadOnly _target As New TextBox()
    Private ReadOnly _controller As New TextBox()
    Private ReadOnly _script As New TextBox With {.Multiline = True, .ReadOnly = True, .WordWrap = False, .ScrollBars = ScrollBars.Both}
    Private ReadOnly _fingerprint As New TextBox()
    Private ReadOnly _copy As New Button With {.Text = "复制脚本", .AutoSize = True}
    Private ReadOnly _trust As New Button With {.Text = "核对证书", .AutoSize = True}
    Private ReadOnly _verify As New Button With {.Text = "验证 Remote Config", .AutoSize = True, .Enabled = False}
    Private ReadOnly _result As New TextBox With {.Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical}
    Private ReadOnly _account As String
    Private ReadOnly _password As String
    Private _busy As Boolean
    Private _trustedTarget As IPAddress

    Public Sub New(target As IPAddress, account As String, password As String)
        _account = account
        _password = password

        Text = "开通 WinRM"
        StartPosition = FormStartPosition.CenterParent
        MinimumSize = New Size(630, 510)
        ClientSize = New Size(720, 550)
        AutoScaleMode = AutoScaleMode.Font
        ShowInTaskbar = False

        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill, .Padding = New Padding(15), .ColumnCount = 2, .RowCount = 9
        }
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For row As Integer = 0 To layout.RowCount - 1
            Select Case row
                Case 4
                    layout.RowStyles.Add(New RowStyle(SizeType.Percent, 65))
                Case 7
                    layout.RowStyles.Add(New RowStyle(SizeType.Percent, 35))
                Case Else
                    layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            End Select
        Next

        Dim instructions As New Label With {
            .Text = "点击「复制脚本」，在目标电脑的管理员 Windows PowerShell 中手动粘贴并执行。然后在这里输入或粘贴目标机输出的 SHA256 指纹。",
            .AutoSize = True, .MaximumSize = New Size(565, 0), .Margin = New Padding(3, 3, 3, 12)
        }
        AddWide(layout, instructions, 0)
        AddField(layout, "目标机 IPv4", _target, 1)
        AddField(layout, "控制端 IPv4", _controller, 2)
        AddWide(layout, _copy, 3)
        _script.Font = New Font(FontFamily.GenericMonospace, Font.Size)
        _script.AccessibleName = "WinRM 初始化脚本"
        _script.Dock = DockStyle.Fill
        AddWide(layout, _script, 4)
        AddField(layout, "目标机 SHA256 指纹", _fingerprint, 5)

        Dim actions As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Dock = DockStyle.Fill}
        actions.Controls.AddRange({_trust, _verify})
        AddWide(layout, actions, 6)
        _result.AccessibleName = "验证结果"
        _result.Dock = DockStyle.Fill
        AddWide(layout, _result, 7)

        Dim closeButton As New Button With {.Text = "关闭", .AutoSize = True, .Anchor = AnchorStyles.Right,
                                            .DialogResult = DialogResult.Cancel}
        AddWide(layout, closeButton, 8)
        CancelButton = closeButton
        Controls.Add(layout)

        If target IsNot Nothing AndAlso target.AddressFamily = AddressFamily.InterNetwork Then
            _target.Text = target.ToString()
            Try
                Using route As New UdpClient(AddressFamily.InterNetwork)
                    route.Connect(target, 5986)
                    _controller.Text = DirectCast(route.Client.LocalEndPoint, IPEndPoint).Address.ToString()
                End Using
            Catch ex As SocketException
                ' The controller address can be entered manually.
            End Try
        End If

        AddHandler _copy.Click, AddressOf CopyScript
        AddHandler _trust.Click, AddressOf VerifyCertificate
        AddHandler _verify.Click, AddressOf VerifyRemoteConfig
        AddHandler _target.TextChanged, AddressOf ResetTarget
        AddHandler _controller.TextChanged, AddressOf ResetTarget
        AddHandler _fingerprint.TextChanged, AddressOf ResetTrust
        AddHandler FormClosing, Sub(sender, e) e.Cancel = _busy
        UpdateScriptPreview()
    End Sub

    Private Shared Sub AddWide(layout As TableLayoutPanel, control As Control, row As Integer)
        layout.Controls.Add(control, 0, row)
        layout.SetColumnSpan(control, 2)
    End Sub

    Private Shared Sub AddField(layout As TableLayoutPanel, label As String, field As TextBox, row As Integer)
        layout.Controls.Add(New Label With {.Text = label, .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, row)
        field.Dock = DockStyle.Fill
        field.AccessibleName = label
        layout.Controls.Add(field, 1, row)
    End Sub

    Private Shared Function ParseIpv4(value As String) As IPAddress
        Dim address As IPAddress = Nothing
        If IPAddress.TryParse(value.Trim(), address) AndAlso address.AddressFamily = AddressFamily.InterNetwork AndAlso
            Not address.Equals(IPAddress.Any) AndAlso Not address.Equals(IPAddress.Broadcast) AndAlso
            Not IPAddress.IsLoopback(address) Then Return address
        Return Nothing
    End Function

    Private Sub ResetTarget(sender As Object, e As EventArgs)
        ResetTrust(sender, e)
        UpdateScriptPreview()
    End Sub

    Private Sub ResetTrust(sender As Object, e As EventArgs)
        _trustedTarget = Nothing
        _verify.Enabled = False
        _result.Clear()
    End Sub

    Private Sub UpdateScriptPreview()
        Dim target As IPAddress = ParseIpv4(_target.Text)
        Dim controller As IPAddress = ParseIpv4(_controller.Text)
        _copy.Enabled = target IsNot Nothing AndAlso controller IsNot Nothing AndAlso Not target.Equals(controller)
        _script.Text = If(_copy.Enabled,
            Properties.ReadRemoteConfigResource("Initialize-WinRmHttps.ps1").
                Replace("__TARGET_IPV4__", target.ToString()).Replace("__CONTROLLER_IPV4__", controller.ToString()).
                Replace(vbCrLf, vbLf).Replace(vbLf, vbCrLf),
            String.Empty)
    End Sub

    Private Sub CopyScript(sender As Object, e As EventArgs)
        Try
            Clipboard.SetText(_script.Text)
            _result.Text = "脚本已复制。请在目标电脑的管理员 Windows PowerShell 中运行。"
        Catch ex As Runtime.InteropServices.ExternalException
            _script.Focus()
            _script.SelectAll()
            _result.Text = "剪贴板暂时不可用。请按 Ctrl+C 复制已选中的脚本。"
        End Try
    End Sub

    Private Async Sub VerifyCertificate(sender As Object, e As EventArgs)
        Dim target As IPAddress = ParseIpv4(_target.Text)
        If target Is Nothing Then
            _result.Text = "请输入有效的目标机 IPv4 地址。"
            Return
        End If
        Dim fingerprint As Match = Regex.Match(_fingerprint.Text.Trim(), "\A(?:SHA256\s*:\s*)?([a-f0-9]{64})\z",
                                               RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        If Not fingerprint.Success Then
            _result.Text = "请手动输入 64 位十六进制指纹，或粘贴目标机输出的 SHA256 行。"
            Return
        End If

        _trustedTarget = Nothing
        SetBusy(True, "正在核对目标地址、证书名称和 SHA256 指纹…")
        Try
            Using certificate As X509Certificate2 = Await System.Threading.Tasks.Task.Run(
                Function() FetchMatchingCertificate(target, 5986, fingerprint.Groups(1).Value))
                Dim prompt As String = $"已核对 {target}:5986 的证书。" & vbCrLf &
                    certificate.Subject & vbCrLf & vbCrLf &
                    "是否将此证书加入当前 Windows 用户的信任库？"
                If MessageBox.Show(Me, prompt, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                   MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then
                    _result.Text = "证书指纹已匹配。未添加信任。"
                    Return
                End If

                Using store As New X509Store(StoreName.Root, StoreLocation.CurrentUser)
                    store.Open(OpenFlags.ReadWrite)
                    If store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, False).Count = 0 Then
                        store.Add(certificate)
                    End If
                    If store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, False).Count = 0 Then
                        Throw New InvalidOperationException("证书未加入当前用户的信任库。")
                    End If
                End Using
                _trustedTarget = target
                _result.Text = "证书已核对并信任。已有 WMI 配置可直接验证。首次配置请返回「关机」标签页点击 Remote Config。"
            End Using
        Catch ex As Exception
            _result.Text = "证书核对失败。" & vbCrLf & ex.GetBaseException().Message
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Shared Function FetchMatchingCertificate(target As IPAddress, port As Integer, fingerprint As String) As X509Certificate2
        Using client As New TcpClient(AddressFamily.InterNetwork)
            Dim connect As System.Threading.Tasks.Task = client.ConnectAsync(target, port)
            If Not connect.Wait(TimeSpan.FromSeconds(5)) Then Throw New TimeoutException($"连接 TCP {port} 超时。")
            Using tls As New SslStream(client.GetStream(), False,
                Function(sender, certificate, chain, errors)
                    If certificate Is Nothing OrElse (errors And SslPolicyErrors.RemoteCertificateNameMismatch) <> 0 Then
                        Return False
                    End If
                    Using sha As SHA256 = SHA256.Create()
                        Dim digest As String = BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-", "")
                        Return String.Equals(digest, fingerprint, StringComparison.OrdinalIgnoreCase)
                    End Using
                End Function)
                ' This first handshake sends no credentials. It accepts an untrusted chain only for an exact fingerprint match.
                Dim handshake As System.Threading.Tasks.Task = tls.AuthenticateAsClientAsync(target.ToString(), Nothing, Global.System.Security.Authentication.SslProtocols.Tls12, False)
                If Not handshake.Wait(TimeSpan.FromSeconds(8)) Then Throw New TimeoutException("读取目标机证书超时。")
                Dim certificate As New X509Certificate2(tls.RemoteCertificate)
                Try
                    Dim constraints As X509BasicConstraintsExtension =
                        TryCast(certificate.Extensions("2.5.29.19"), X509BasicConstraintsExtension)
                    Dim usage As X509EnhancedKeyUsageExtension =
                        TryCast(certificate.Extensions("2.5.29.37"), X509EnhancedKeyUsageExtension)
                    Dim serverAuth As Boolean = False
                    If usage IsNot Nothing Then
                        For Each oid As Oid In usage.EnhancedKeyUsages
                            If oid.Value = "1.3.6.1.5.5.7.3.1" Then serverAuth = True
                        Next
                    End If
                    If (constraints IsNot Nothing AndAlso constraints.CertificateAuthority) OrElse Not serverAuth OrElse
                        DateTime.Now < certificate.NotBefore OrElse DateTime.Now > certificate.NotAfter Then
                        Throw New InvalidOperationException("证书不是有效的服务器身份验证证书。")
                    End If
                    Return certificate
                Catch
                    certificate.Dispose()
                    Throw
                End Try
            End Using
        End Using
    End Function

    Private Async Sub VerifyRemoteConfig(sender As Object, e As EventArgs)
        Dim target As IPAddress = _trustedTarget
        Dim controller As IPAddress = ParseIpv4(_controller.Text)
        If target Is Nothing OrElse controller Is Nothing OrElse target.Equals(controller) Then
            _result.Text = "请核对证书，并填写与目标机不同的控制端 IPv4 地址。"
            Return
        End If
        If String.IsNullOrWhiteSpace(_account) OrElse String.IsNullOrEmpty(_password) Then
            _result.Text = "请返回「主机属性 → 关机」填写目标机管理员用户名和密码，再打开此向导。"
            Return
        End If

        SetBusy(True, "正在只读检查 WinRM、管理员令牌设置、WMI 防火墙和 Windows 启动时间…")
        Try
            Dim result As Tuple(Of Boolean, String) = Await System.Threading.Tasks.Task.Run(
                Function() Properties.RunRemoteConfig(target.ToString(), target.ToString(), _account, _password,
                                                       {controller.ToString()}, useHttp:=False, verifyOnly:=True))
            _result.Text = If(result.Item1, "验证通过。", "验证未通过。") & vbCrLf & result.Item2
        Catch ex As Exception
            _result.Text = "验证未通过。" & vbCrLf & ex.GetBaseException().Message
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub SetBusy(busy As Boolean, Optional status As String = Nothing)
        _busy = busy
        _target.ReadOnly = busy
        _controller.ReadOnly = busy
        _fingerprint.ReadOnly = busy
        _copy.Enabled = Not busy AndAlso _script.TextLength > 0
        _trust.Enabled = Not busy
        _verify.Enabled = Not busy AndAlso _trustedTarget IsNot Nothing
        If status IsNot Nothing Then _result.Text = status
    End Sub
End Class

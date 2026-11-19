'    WakeOnLAN - Wake On LAN
'    Copyright (C) 2004-2019 Aquila Technology, LLC. <webmaster@aquilatech.com>
'
'    This file is part of WakeOnLAN.
'
'    WakeOnLAN is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    WakeOnLAN is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with WakeOnLAN.  If not, see <http://www.gnu.org/licenses/>.

Imports Machines
Imports System.Linq
Imports System.Text
Imports System.Threading.Tasks

Public Class Properties
    Private Shared _remoteConfigRunning As Boolean
    Private ReadOnly _wmiAllowedAddresses As New TextBox()
    Private _previousHostName As String
    Private ReadOnly _encryption As New Encryption()

    Public Sub New()
        InitializeComponent()

        Dim remoteConfigButton As New Button With {
            .Text = "Remote Config",
            .AutoSize = True,
            .Location = New Drawing.Point(tDomain.Right + 8, tDomain.Top),
            .CausesValidation = False,
            .UseVisualStyleBackColor = True
        }
        AddHandler remoteConfigButton.Click,
            Sub(sender, e) ConfigureRemoteHost(Me, tHostURI.Text, IP.Text, tUserId.Text, tPassword.Text, tDomain.Text,
                                              _wmiAllowedAddresses.Text)
        TabShutdown.Controls.Add(remoteConfigButton)
        Dim winRmSetupButton As New Button With {
            .Text = "开通 WinRM",
            .AutoSize = True,
            .Location = New Drawing.Point(remoteConfigButton.Left + remoteConfigButton.GetPreferredSize(Drawing.Size.Empty).Width + 6, remoteConfigButton.Top),
            .CausesValidation = False,
            .UseVisualStyleBackColor = True
        }
        AddHandler winRmSetupButton.Click, AddressOf ShowWinRmSetup
        TabShutdown.Controls.Add(winRmSetupButton)

        Dim allowedSourcesLabel As New Label With {
            .Text = If(Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName = "zh",
                       "WMI 允许来源 IP", "Allowed WMI source IPs"),
            .AutoSize = True,
            .Location = New Drawing.Point(lbShutdownCommand.Left, tDomain.Bottom + 10)
        }
        _wmiAllowedAddresses.Location = New Drawing.Point(allowedSourcesLabel.Right + 8, allowedSourcesLabel.Top - 3)
        _wmiAllowedAddresses.Width = TextBox_Command.Right - _wmiAllowedAddresses.Left
        _wmiAllowedAddresses.AccessibleName = allowedSourcesLabel.Text
        TabShutdown.Controls.AddRange({allowedSourcesLabel, _wmiAllowedAddresses})
    End Sub

    Private Sub ShowWinRmSetup(sender As Object, e As EventArgs)
        Dim target As Net.IPAddress = Nothing
        If Not Net.IPAddress.TryParse(tHostURI.Text.Trim(), target) Then
            Net.IPAddress.TryParse(IP.Text.Trim(), target)
        End If
        Dim account As String = tUserId.Text.Trim()
        If Not String.IsNullOrWhiteSpace(tDomain.Text) AndAlso Not account.Contains("\") AndAlso Not account.Contains("@") Then
            account = tDomain.Text.Trim() & "\" & account
        End If
        Using wizard As New WinRmSetup(target, account, tPassword.Text)
            wizard.ShowDialog(Me)
        End Using
    End Sub

    Friend Shared Async Sub ConfigureRemoteHost(owner As Form, computerName As String,
                                                targetAddress As String, userName As String,
                                                password As String, domain As String, allowedSources As String,
                                                Optional useHttp As Boolean = False)
        Const title As String = "Remote Config"
        If _remoteConfigRunning Then Return

        computerName = computerName.Trim()
        If String.IsNullOrWhiteSpace(computerName) Then computerName = targetAddress.Trim()
        userName = userName.Trim()
        If String.IsNullOrWhiteSpace(computerName) OrElse String.IsNullOrWhiteSpace(userName) OrElse
            (Not useHttp AndAlso String.IsNullOrEmpty(password)) Then
            MessageBox.Show(owner, "Enter the target host and its administrator credentials in Host Properties.",
                            title, MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        If Not String.IsNullOrWhiteSpace(domain) AndAlso Not userName.Contains("\") AndAlso Not userName.Contains("@") Then
            userName = domain.Trim() & "\" & userName
        End If

        Dim address As Net.IPAddress = Nothing
        If useHttp AndAlso Net.IPAddress.TryParse(computerName, address) Then
            MessageBox.Show(owner, "WinRM HTTP with Kerberos requires the target DNS hostname. Set it in Host Properties.",
                            title, MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        If Net.IPAddress.TryParse(computerName, address) Then targetAddress = computerName
        targetAddress = Interaction.InputBox("Enter the target PC IPv4 address.", title, targetAddress).Trim()
        If targetAddress.Length = 0 Then Return
        If Not Net.IPAddress.TryParse(targetAddress, address) OrElse
            address.AddressFamily <> Net.Sockets.AddressFamily.InterNetwork Then
            MessageBox.Show(owner, "Enter a valid target IPv4 address.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        If String.IsNullOrWhiteSpace(allowedSources) Then
            allowedSources = Interaction.InputBox(
                "Enter the controller IP addresses allowed through the target WMI firewall, separated by commas.", title)
        End If
        If String.IsNullOrWhiteSpace(allowedSources) Then Return
        Dim allowed As String() = ParseAllowedSources(allowedSources)
        If allowed Is Nothing Then
            MessageBox.Show(owner, "请输入有效的控制端 IP 地址，多个地址用逗号分隔。",
                            title, MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        Dim confirmation As String = $"Configure WMI shutdown on {computerName} ({targetAddress}) for {userName}?" &
            vbCrLf & $"Allowed controllers: {String.Join(", ", allowed)}" & vbCrLf & vbCrLf &
            "This changes the target's WMI, DCOM, firewall, and local administrator remote access settings. " &
            "First-time setup saves a backup on the target. For an existing configuration, only the allowed WMI source IPs are updated. " &
            If(useHttp, "WinRM HTTP must already be available. The current Windows account needs remote administrator access via Kerberos.",
                        "WinRM over HTTPS must already be available.")
        If MessageBox.Show(owner, confirmation, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                           MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return

        Dim mainWindow As Form = Application.OpenForms.OfType(Of Explorer)().FirstOrDefault()
        Dim result As Tuple(Of Boolean, String)
        _remoteConfigRunning = True
        Try
            result = Await System.Threading.Tasks.Task.Run(
                Function() RunRemoteConfig(computerName, targetAddress, userName, password, allowed, useHttp))
        Catch ex As Exception
            result = Tuple.Create(False, ex.Message)
        Finally
            _remoteConfigRunning = False
        End Try

        Dim resultOwner As Form = GetRemoteConfigResultOwner(owner, mainWindow)
        If resultOwner Is Nothing Then Return
        MessageBox.Show(resultOwner, result.Item2, title, MessageBoxButtons.OK,
                        If(result.Item1, MessageBoxIcon.Information, MessageBoxIcon.Error))
    End Sub

    Private Shared Function GetRemoteConfigResultOwner(owner As Form, mainWindow As Form) As Form
        For Each candidate As Form In {owner, mainWindow}
            If candidate IsNot Nothing AndAlso Not candidate.IsDisposed AndAlso
                Not candidate.Disposing AndAlso candidate.IsHandleCreated Then Return candidate
        Next
        Return Nothing
    End Function

    Friend Shared Function RunRemoteConfig(computerName As String, targetAddress As String, userName As String,
                                           password As String, allowed As String(), useHttp As Boolean,
                                           Optional verifyOnly As Boolean = False) As Tuple(Of Boolean, String)
        Dim configScript As String = ReadRemoteConfigResource("Configure-RemoteWmi.ps1")
        Dim launcher As String = ReadRemoteConfigResource("Invoke-RemoteWmiConfig.ps1")
        Dim windows As String = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        Dim systemFolder As String = If(Environment.Is64BitOperatingSystem AndAlso Not Environment.Is64BitProcess,
                                        "Sysnative", "System32")
        Dim startInfo As New ProcessStartInfo With {
            .FileName = IO.Path.Combine(windows, systemFolder, "WindowsPowerShell\v1.0\powershell.exe"),
            .Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " &
                Convert.ToBase64String(Encoding.Unicode.GetBytes(launcher)),
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardInput = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        Using child As Process = Process.Start(startInfo)
            Dim stdout As Task(Of String) = child.StandardOutput.ReadToEndAsync()
            Dim stderr As Task(Of String) = child.StandardError.ReadToEndAsync()
            Using input As IO.StreamWriter = child.StandardInput
                For Each value As String In {If(useHttp, "HTTP", "HTTPS"), computerName, targetAddress, userName, password, String.Join(",", allowed), configScript, If(verifyOnly, "Verify", "Apply")}
                    input.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)))
                Next
            End Using
            child.WaitForExit()
            Dim output As String = stdout.GetAwaiter().GetResult().Trim()
            Dim errors As String = stderr.GetAwaiter().GetResult().Trim()
            If child.ExitCode = 0 Then Return Tuple.Create(True, output)

            Dim summary As String
            If child.ExitCode = 2 Then
                Dim endpoint As String = If(useHttp, "WinRM HTTP (TCP 5985)", "WinRM HTTPS (TCP 5986)")
                Dim requirement As String = If(useHttp,
                    "Check the firewall, target DNS hostname, and Kerberos administrator access.",
                    "Check the target HTTPS listener, firewall, and that the certificate is trusted and matches the connection address.")
                summary = If(verifyOnly, $"无法连接 {computerName} 的 {endpoint}。验证未开始。",
                             $"无法连接 {computerName} 的 {endpoint}。未运行配置脚本。") & vbCrLf & requirement
            ElseIf child.ExitCode = 3 Then
                summary = If(verifyOnly, $"已连接 {computerName}，但 Remote Config 验证未通过。",
                             $"已连接 {computerName}，但远程配置未完成。请查看下方输出和目标机备份路径。")
            Else
                summary = "无法启动远程配置。请查看 PowerShell 输出。"
            End If

            Dim details As String = (output & vbCrLf & errors).Trim()
            Return Tuple.Create(False, summary & vbCrLf & vbCrLf & details)
        End Using
    End Function

    Friend Shared Function ReadRemoteConfigResource(name As String) As String
        Using stream As IO.Stream = GetType(Properties).Assembly.GetManifestResourceStream(name)
            If stream Is Nothing Then Throw New IO.FileNotFoundException("Missing Remote Config resource: " & name)
            Using reader As New IO.StreamReader(stream)
                Return reader.ReadToEnd()
            End Using
        End Using
    End Function

    Private Shared Function ParseAllowedSources(value As String) As String()
        Dim addresses As New System.Collections.Generic.List(Of String)()
        For Each entry As String In value.Split(","c, "，"c)
            Dim address As Net.IPAddress = Nothing
            If Not Net.IPAddress.TryParse(entry.Trim(), address) OrElse address.Equals(Net.IPAddress.Any) OrElse
                address.Equals(Net.IPAddress.IPv6Any) Then Return Nothing
            addresses.Add(address.ToString())
        Next
        Return addresses.Distinct().ToArray()
    End Function

    Private Sub OK_Button_Click(ByVal sender As Object, ByVal e As EventArgs) Handles OK_Button.Click
        Dim m As New Machine
        If Not String.IsNullOrWhiteSpace(_wmiAllowedAddresses.Text) Then
            Dim allowed As String() = ParseAllowedSources(_wmiAllowedAddresses.Text)
            If allowed Is Nothing Then
                MessageBox.Show(Me, "请输入有效的控制端 IP 地址，多个地址用逗号分隔。",
                                "Remote Config", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                _wmiAllowedAddresses.Focus()
                Return
            End If
            m.WmiAllowedAddresses = String.Join(", ", allowed)
        End If

        Try
            Machines.Remove(_previousHostName)

            m.Name = MachineName.Text
            m.MAC = MAC.Text
            m.IP = IP.Text

            If (rbIP.Checked) Then
                m.Method = 0
            Else
                m.Method = 1
            End If

            m.Broadcast = Broadcast.Text
            m.Netbios = tHostURI.Text
            m.KeepAlive = cbKeepAlive.Checked
            m.Emergency = CheckBox_Emergency.Checked
            m.ShutdownCommand = TextBox_Command.Text
            m.Group = Group.Text
            m.UDPPort = CInt(UDPPort.Text)
            m.TTL = CInt(TTL.Text)
            m.RemoteCommand = tRDPFilename.Text.Trim
            m.Note = TextBox_Notes.Text
            m.UserID = tUserId.Text
            m.Password = _encryption.Encrypt(tPassword.Text)
            m.Domain = tDomain.Text
            m.ShutdownMethod = ComboBoxShutdownMethod.SelectedIndex
            m.RepeatCount = Repeat.Text
            Machines.Add(m)

            Machines.Save()
            DialogResult = DialogResult.OK
            Close()

        Catch ex As Exception
            MessageBox.Show(ex.Message, My.Resources.Strings.lit_Error, MessageBoxButtons.OK, MessageBoxIcon.Error)

        End Try

    End Sub

    Private Sub Cancel_Button_Click(ByVal sender As Object, ByVal e As EventArgs) Handles Cancel_Button.Click
        DialogResult = DialogResult.Cancel
        Close()
    End Sub

    Public Sub Create()
        Try
            _previousHostName = String.Empty
            Text = String.Format(My.Resources.Strings.Properties, My.Resources.Strings.isNew)
            Delete_Button.Visible = False
            cbKeepAlive.Checked = False
            IP.Text = String.Empty
            _wmiAllowedAddresses.Clear()
            Broadcast.Text = Net.IPAddress.Broadcast.ToString()
            rbIP.Checked = True
            UDPPort.Text = "9"
            TTL.Text = "128"
            tRDPFilename.Text = String.Empty
            ComboBoxShutdownMethod.SelectedIndex = 0
            TextBox_Notes.Text = String.Empty
            Repeat.Text = "1"
            ShowDialog(My.Forms.Explorer)

        Catch ex As Exception
            MessageBox.Show(ex.InnerException.ToString() & vbCrLf & ex.Message & vbCrLf & ex.StackTrace, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)

        End Try
    End Sub

    Public Sub Edit(ByVal hostName As String)
        Dim m As Machine

        _previousHostName = hostName
        Text = String.Format(My.Resources.Strings.Properties, hostName)

        m = Machines(hostName)
        MachineName.Text = m.Name
        MAC.Text = m.MAC
        IP.Text = m.IP
        _wmiAllowedAddresses.Text = m.WmiAllowedAddresses
        Broadcast.Text = m.Broadcast
        tHostURI.Text = m.Netbios
        cbKeepAlive.Checked = m.KeepAlive
        CheckBox_Emergency.Checked = m.Emergency
        TextBox_Command.Text = m.ShutdownCommand
        Group.Text = m.Group
        UDPPort.Text = m.UDPPort
        TTL.Text = m.TTL
        rbIP.Checked = (m.Method = 0)
        rbURI.Checked = (m.Method = 1)
        tRDPFilename.Text = m.RemoteCommand
        TextBox_Notes.Text = m.Note
        tUserId.Text = m.UserID
        tPassword.Text = _encryption.Decrypt(m.Password)
        tDomain.Text = m.Domain
        ComboBoxShutdownMethod.SelectedIndex = m.ShutdownMethod
        Repeat.Text = m.RepeatCount

        ValidateChildren()
        ShowDialog(My.Forms.Explorer)
    End Sub

    Private Sub Delete_Button_Click(ByVal sender As Object, ByVal e As EventArgs) Handles Delete_Button.Click
        If MessageBox.Show(String.Format(My.Resources.Strings.AreYouSure), String.Format(My.Resources.Strings.Delete, 1), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) = DialogResult.Yes Then
            Machines.Remove(_previousHostName)
            Machines.Save()
            DialogResult = DialogResult.OK
        Else
            DialogResult = DialogResult.Cancel
        End If

        Close()
    End Sub

    Private Sub Controls_Validating(sender As Object, e As System.ComponentModel.CancelEventArgs) Handles MachineName.Validating, tHostURI.Validating, MAC.Validating, Broadcast.Validating, IP.Validating, UDPPort.Validating, TTL.Validating, tRDPFilename.Validating, Repeat.Validating
        DoValidation(sender)
    End Sub

    Private Sub ControlTextChanged(sender As Object, e As EventArgs) Handles MAC.TextChanged, UDPPort.TextChanged, TTL.TextChanged, tHostURI.TextChanged, Repeat.TextChanged
        DoValidation(sender)
    End Sub

    Private Sub MachineName_TextChanged(sender As Object, e As EventArgs) Handles MachineName.TextChanged
        Text = String.Format(My.Resources.Strings.Properties, MachineName.Text)
        DoValidation(sender)
    End Sub

    Private Sub DoValidation(sender As Object)
        If sender.IsValid() Then
            ErrorProvider1.SetError(sender, String.Empty)
        Else
            ErrorProvider1.SetError(sender, sender.ErrorMessage)
        End If

        For Each tab As TabPage In TabControl1.TabPages
            For Each c As Control In tab.Controls
                If ErrorProvider1.GetError(c).Length Then
                    OK_Button.Enabled = False

                    Dim rc As Rectangle = TabControl1.GetTabRect(tab.TabIndex)
                    ErrorProvider1.SetIconPadding(TabControl1, -rc.Right)
                    ErrorProvider1.SetError(TabControl1, ErrorProvider1.GetError(c).ToString())
                    Exit Sub
                End If
            Next
        Next

        EditRDPButton.Enabled = (tRDPFilename.Text.ToLower.EndsWith(".rdp"))
        ErrorProvider1.SetError(TabControl1, String.Empty)

        If String.IsNullOrEmpty(MachineName.Text) Then Return
        If String.IsNullOrEmpty(MAC.Text) Then Return
        If String.IsNullOrEmpty(tHostURI.Text) Then Return
        If String.IsNullOrEmpty(Broadcast.Text) Then Return

        OK_Button.Enabled = True
    End Sub

    Private Sub bCalcBroadcast_Click(sender As Object, e As EventArgs) Handles bCalcBroadcast.Click
        CalcSubnet.ShowDialog(Me)
    End Sub

    Private Sub Help_Button_Click(sender As Object, e As EventArgs) Handles Help_Button.Click
        ShowHelp(Me, "properties\default.html")
    End Sub

    Private Sub File_Button_Click(sender As Object, e As EventArgs) Handles File_Button.Click
        Dim openFileDialog As New OpenFileDialog

        With openFileDialog
            .Title = My.Resources.Strings.rdp_title
            .CheckFileExists = True
            .CheckPathExists = True
            .DefaultExt = "rdp"
            .Filter = My.Resources.Strings.rdp_filter
            .FilterIndex = 1
            .RestoreDirectory = True

            Dim dialogResult As DialogResult = .ShowDialog()
            If (dialogResult = DialogResult.OK) Then
                tRDPFilename.Text = .FileName
            End If
        End With
    End Sub

    Private Sub EditRDPButton_Click(sender As Object, e As EventArgs) Handles EditRDPButton.Click
        Shell(String.Format("mstsc.exe /edit ""{0}""", tRDPFilename.Text), AppWinStyle.NormalFocus, False)
    End Sub

End Class

Public Class ComboboxItem
    Public Property Text() As String
        Get
            Return m_Text
        End Get
        Set(value As String)
            m_Text = value
        End Set
    End Property

    Private m_Text As String
    Public Property Value() As Object
        Get
            Return m_Value
        End Get
        Set(value As Object)
            m_Value = value
        End Set
    End Property

    Private m_Value As Object

    Public Overrides Function ToString() As String
        Return Text
    End Function
End Class
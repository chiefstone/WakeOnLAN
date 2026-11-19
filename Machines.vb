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

Imports System.IO
Imports System.Xml.Serialization
Imports System.Net
Imports System.Linq
Imports Machines
Imports System.Threading
Imports System.Management

Module MachinesModule
    Public Machines As New MachinesClass
End Module

Public Class MachinesClass
    Inherits CollectionBase

    Public Dirty As Boolean = False
    Private Shared _pool As Semaphore

    Default Public Property Item(ByVal Name As String) As Machine
        Get
            For Each m As Machine In From m1 As Machine In List Where m1.Name = Name
                Return m
            Next
            Return Nothing
        End Get
        Set(ByVal value As Machine)
            List(Name) = value
        End Set
    End Property

    Default Public ReadOnly Property Item(ByVal Index As Integer) As Machine
        Get
            Return List(Index)
        End Get
    End Property

    Public Sub Load(pool As Semaphore)
        _pool = pool
        Dim filename As String = GetFile()
        If File.Exists(filename) Then
            Import(filename)
        End If
    End Sub

    Public Sub Save()
        If Not Dirty Then Exit Sub
        Export(GetFile)
        Dirty = False
    End Sub

    Friend Shared ReadOnly Property DefaultDatabasePath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                "Aquila Technology", "WakeOnLan", "machines.xml")
        End Get
    End Property

    Friend Shared Function ResolveDatabasePath(configuredPath As String) As String
        If File.Exists(configuredPath) Then Return configuredPath

        Dim bundledPath As String = Path.Combine(Path.GetDirectoryName(GetType(MachinesClass).Assembly.Location), "machines.xml")
        Return If(File.Exists(bundledPath), bundledPath, configuredPath)
    End Function

    Public Function GetFile() As String
        If String.IsNullOrEmpty(My.Settings.dbPath) Then
            My.Settings.dbPath = ResolveDatabasePath(DefaultDatabasePath)
        End If

        Return My.Settings.dbPath
    End Function

    Public Sub Import(filename As String)
        Try
            Using fileStream As New FileStream(filename, FileMode.Open, FileAccess.Read)
                Dim serializer As New XmlSerializer(GetType(MachinesClass))
                Machines = CType(serializer.Deserialize(fileStream), MachinesClass)
            End Using
            CheckUpgrade()

        Catch ex As Exception
			Tracelog.WriteLine("Machines::Import " & ex.Message)

        End Try

    End Sub

    Private Sub CheckUpgrade()
		Tracelog.WriteLine("Loading database")
		Tracelog.Indent()

		For Each machine As Machine In Machines
            PrepareMonitor(machine)

            If (machine.Broadcast = "") Then
                Dirty = True
                machine.Broadcast = IPAddress.Broadcast.ToString()
            End If
            If (machine.UDPPort = 0) Then
                Dirty = True
                machine.UDPPort = 9
            End If
            If (machine.TTL = 0) Then
                Dirty = True
                machine.TTL = 128
            End If

            Tracelog.WriteLine("Machine: " & machine.Name)
			Tracelog.Indent()
			Tracelog.WriteLine("MAC [" & machine.MAC & "]")
			Tracelog.WriteLine("IP [" & machine.IP & "]")
			Tracelog.WriteLine("Broadcast [" & machine.Broadcast & "]")
			Tracelog.WriteLine("Netbios [" & machine.Netbios & "]")
			Tracelog.WriteLine("Method [" & machine.Method & "]")
			Tracelog.WriteLine("Emergency [" & machine.Emergency & "]")
			Tracelog.WriteLine("ShutdownCommand [" & machine.ShutdownCommand & "]")
			Tracelog.WriteLine("Group [" & machine.Group & "]")
			Tracelog.WriteLine("UDPPort [" & machine.UDPPort & "]")
			Tracelog.WriteLine("TTL [" & machine.TTL & "]")
            Tracelog.WriteLine("RemoteCommand [" & machine.RemoteCommand & "]")
            Tracelog.WriteLine("Note [" & machine.Note & "]")
            Tracelog.WriteLine("UserID [" & machine.UserID & "]")
			Tracelog.WriteLine("Domain [" & machine.Domain & "]")
			Tracelog.WriteLine("ShutdownMethod [" & machine.ShutdownMethod & "]")
			Tracelog.WriteLine("KeepAlive [" & machine.KeepAlive & "]")
			Tracelog.WriteLine("RepeatCount [" & machine.RepeatCount & "]")
			Tracelog.UnIndent()
		Next

		Tracelog.UnIndent()
		Tracelog.WriteLine("Database complete")
		Tracelog.Line()
	End Sub

    Public Sub Export(ByVal filename As String)
        Dim serializer As New XmlSerializer(GetType(MachinesClass))
        Dim writer As StreamWriter

        Try
            writer = New StreamWriter(filename)
            serializer.Serialize(writer, Machines)
            writer.Close()

        Catch ex As UnauthorizedAccessException
            If (MessageBox.Show(ex.Message, My.Resources.Strings.ChangesNotSaved, MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Error) = DialogResult.Retry) Then
                Export(filename)
            End If

        Catch ex As Exception
            MessageBox.Show(ex.Message, My.Resources.Strings.ChangesNotSaved, MessageBoxButtons.OK, MessageBoxIcon.Error)

        End Try

    End Sub

    Public Sub Add(ByVal machine As Machine)
        List.Add(machine)
		AddHandler machine.StatusChange, AddressOf My.Forms.Explorer.StatusChange
		AddHandler machine.TraceEvent, AddressOf My.Forms.Explorer.TraceEvent

		If My.Forms.Explorer.PingToolStripButton.Checked Then
            PrepareMonitor(machine)
            machine.Run()
        End If
        Dirty = True

    End Sub

    Private Shared Sub PrepareMonitor(machine As Machine)
        machine.Pool = _pool
        machine.WindowsStatusProbe = Function(target) ProbeWindows(machine, target)
    End Sub

    Private Shared Function ProbeWindows(machine As Machine, target As String) As Machine.StatusCodes
        Dim options As New ConnectionOptions With {
            .Authentication = AuthenticationLevel.PacketPrivacy,
            .Impersonation = ImpersonationLevel.Impersonate
        }
        If Not String.IsNullOrWhiteSpace(machine.UserID) Then
            Dim account As String = machine.UserID.Trim()
            If Not String.IsNullOrWhiteSpace(machine.Domain) AndAlso
                Not account.Contains("\") AndAlso Not account.Contains("@") Then
                account = machine.Domain.Trim() & "\" & account
            End If
            options.Username = account
            options.Password = New Encryption().Decrypt(machine.Password)
        End If

        Try
            Dim scope As New ManagementScope("\\" & target & "\root\cimv2", options)
            Dim query As New ObjectQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem")
            Dim enumeration As New EnumerationOptions With {.Timeout = TimeSpan.FromSeconds(5), .Rewindable = False}
            Using searcher As New ManagementObjectSearcher(scope, query, enumeration)
                Using systems As ManagementObjectCollection = searcher.Get()
                    For Each system As ManagementObject In systems
                        Using system
                            If Not String.IsNullOrWhiteSpace(TryCast(system("LastBootUpTime"), String)) Then
                                Return Machine.StatusCodes.Online
                            End If
                        End Using
                    Next
                End Using
            End Using
        Catch ex As Runtime.InteropServices.COMException When ex.ErrorCode = &H800706BA
            ' RPC server unavailable. Authentication and other errors remain unknown.
            Return Machine.StatusCodes.Offline
        End Try

        Return Machine.StatusCodes.Unknown
    End Function

    Public Sub Update(ByVal machine As Machine)
        Dirty = True
    End Sub

    Public Sub Remove(ByVal name As String)
        Dim machine As Machine = Machines(name)

        If machine Is Nothing Then Return
        machine.Cancel()
        RemoveHandler machine.StatusChange, AddressOf My.Forms.Explorer.StatusChange
        List.Remove(machine)
        Dirty = True
    End Sub

    Public Sub Close()
        For Each machine As Machine In List
            machine.Cancel()
            RemoveHandler machine.StatusChange, AddressOf My.Forms.Explorer.StatusChange
        Next
    End Sub

End Class

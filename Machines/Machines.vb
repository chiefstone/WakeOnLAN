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

Imports System.ComponentModel
Imports System.Net.NetworkInformation
Imports System.Xml.Serialization
Imports System.Net
Imports System.Threading

<DebuggerDisplay("{Name,nq}")>
<Serializable()>
<CLSCompliant(True)>
Public Class Machine

    <NonSerialized> Private WithEvents _backgroundWorker As New BackgroundWorker
    <NonSerialized> Private ReadOnly ping As New Ping
    <NonSerialized> Private _monitoringRequested As Boolean
    <NonSerialized> Private _pendingStatus As StatusCodes = StatusCodes.Unknown
    <NonSerialized> Private _pendingAddress As IPAddress

    Private Declare Function SendARP Lib "iphlpapi.dll" (ByVal DestIP As Int32, ByVal SrcIP As Int32, ByVal pMacAddr As Byte(), ByRef PhyAddrLen As Integer) As Integer

    Public Enum ShutdownMethods As Integer
        WMI = 0
        Custom = 1
        Legacy = 2
    End Enum

	Public Enum StatusCodes As Integer
		Online
		Offline
		Unknown
		Fail
		Uninitialized
	End Enum

	Public Enum TraceMethods As Integer
		WriteLine
		Indent
		UnIndent
	End Enum

	Public Property Name As String = String.Empty
    Public Property MAC As String = String.Empty
    Public Property IP As String = String.Empty
    Public Property Broadcast As String = String.Empty
    Public Property Netbios As String = String.Empty
    Public Property Method As Integer = 0
    Public Property Emergency As Boolean = False
    Public Property ShutdownCommand As String = String.Empty
    Public Property Group As String = String.Empty
    Public Property UDPPort As Integer = 9
    Public Property TTL As Integer = 128
    Public Property RemoteCommand As String = String.Empty
    Public Property Note As String = String.Empty
    Public Property UserID As String = String.Empty
    Public Property Password As String = String.Empty
    Public Property Domain As String = String.Empty
    Public Property ShutdownMethod As ShutdownMethods = ShutdownMethods.WMI
    Public Property WmiAllowedAddresses As String = String.Empty
    Public Property KeepAlive As Boolean = False
    Public Property RepeatCount As Integer = 1

	<XmlIgnore()> Public Status As StatusCodes = StatusCodes.Uninitialized
	<NonSerialized> <XmlIgnore()> Public Reply As PingReply
    <NonSerialized> <XmlIgnore()> Public Pool As Semaphore
    <NonSerialized> <XmlIgnore()> Public WindowsStatusProbe As Func(Of String, StatusCodes)
    <NonSerialized> <XmlIgnore()> Const data As String = "abcdefghijklmnopqrstuvwabcdefghi"
	<NonSerialized> <XmlIgnore()> Dim buffer As Byte() = Text.Encoding.ASCII.GetBytes(data)
	<NonSerialized> <XmlIgnore()> Public message As String

	Public Event StatusChange(ByVal Name As String, ByVal status As StatusCodes, ByVal IpAddress As String)
	Public Event TraceEvent(ByVal s As String, ByVal method As TraceMethods)

	Public Sub New()
        _backgroundWorker.WorkerSupportsCancellation = True
    End Sub

    Public Overrides Function ToString() As String
        Return "Machine: " & Name
    End Function

    Public Sub Run()
        _monitoringRequested = True
		If _backgroundWorker.IsBusy Then Exit Sub

		_backgroundWorker.WorkerReportsProgress = True
        Dim target As String = If(String.IsNullOrWhiteSpace(IP), Netbios, IP)
        If Not String.IsNullOrWhiteSpace(target) Then _backgroundWorker.RunWorkerAsync(target.Trim())
    End Sub

    Public Function IsBusy() As Boolean
        Return _backgroundWorker.IsBusy
    End Function

    Public Sub Cancel()
        _monitoringRequested = False
        If _backgroundWorker.IsBusy Then _backgroundWorker.CancelAsync()
    End Sub

    Public Sub RefreshStatus()
        If _backgroundWorker.IsBusy Then _backgroundWorker.CancelAsync()
        ResetStatus()
        If _monitoringRequested AndAlso Not _backgroundWorker.IsBusy Then Run()
    End Sub

    Private Sub DoWork(sender As Object, e As DoWorkEventArgs) Handles _backgroundWorker.DoWork
        While Not _backgroundWorker.CancellationPending
            Pool.WaitOne()
            Try
                If _backgroundWorker.CancellationPending Then Return

                Dim probe As Func(Of String, StatusCodes) = WindowsStatusProbe
                If probe IsNot Nothing AndAlso ShutdownMethod <> ShutdownMethods.Custom Then
                    _backgroundWorker.ReportProgress(CInt(probe(CStr(e.Argument))))
                Else
                    ProbeWithPing(CStr(e.Argument))
                End If
            Catch ex As Exception
                _backgroundWorker.ReportProgress(CInt(StatusCodes.Fail), ex.Message)
            Finally
                Pool.Release()
            End Try

            If Not _backgroundWorker.CancellationPending Then Thread.Sleep(1000)
        End While
    End Sub

    Private Sub ProbeWithPing(target As String)
        Dim options As New PingOptions With {.Ttl = TTL, .DontFragment = True}
        Dim response As PingReply = ping.Send(target, 3000, buffer, options)
        Dim observedStatus As StatusCodes

        Select Case response.Status
            Case IPStatus.Success
                observedStatus = If(MatchesConfiguredMac(response.Address), StatusCodes.Online, StatusCodes.Unknown)
            Case IPStatus.TimedOut, IPStatus.DestinationNetworkUnreachable, IPStatus.DestinationHostUnreachable
                observedStatus = StatusCodes.Offline
            Case Else
                observedStatus = StatusCodes.Unknown
        End Select

        _backgroundWorker.ReportProgress(CInt(observedStatus), response)
    End Sub

    Private Function MatchesConfiguredMac(address As IPAddress) As Boolean
        If Not String.IsNullOrWhiteSpace(IP) OrElse String.IsNullOrWhiteSpace(MAC) OrElse
            address.AddressFamily <> Sockets.AddressFamily.InterNetwork Then Return True

        Dim remoteMac(5) As Byte
        Dim length = remoteMac.Length
        Dim result = SendARP(BitConverter.ToInt32(address.GetAddressBytes(), 0), 0, remoteMac, length)
        ' ERROR_BAD_NET_NAME can occur when the responding host is on another subnet.
        If result = 0 Then Return CompareMac(BitConverter.ToString(remoteMac, 0, length), MAC) = 0
        Return result = 67
    End Function

    Private Sub backgroundWorker_ProgressChanged(sender As Object, e As ProgressChangedEventArgs) Handles _backgroundWorker.ProgressChanged
        If _backgroundWorker.CancellationPending Then Return

        Dim observedStatus = CType(e.ProgressPercentage, StatusCodes)
        Dim response As PingReply = TryCast(e.UserState, PingReply)
        message = TryCast(e.UserState, String)
        If observedStatus = StatusCodes.Fail Then observedStatus = StatusCodes.Unknown
        Dim respondingAddress As IPAddress = If(observedStatus = StatusCodes.Online AndAlso response IsNot Nothing, response.Address, Nothing)

        If observedStatus <> StatusCodes.Unknown Then
            Dim sameObservation As Boolean = observedStatus = _pendingStatus AndAlso
                Object.Equals(respondingAddress, _pendingAddress)
            _pendingStatus = observedStatus
            _pendingAddress = respondingAddress

            ' Confirm transitions with two consecutive observations of the same result.
            If Not sameObservation Then Return
        Else
            _pendingStatus = StatusCodes.Unknown
            _pendingAddress = Nothing
        End If

        Dim addressChanged As Boolean = observedStatus = StatusCodes.Online AndAlso
            Not Object.Equals(If(Reply Is Nothing, Nothing, Reply.Address), respondingAddress)
        Reply = If(observedStatus = StatusCodes.Online, response, Nothing)
        If Status = observedStatus AndAlso Not addressChanged Then Return

        Status = observedStatus
        Dim address As String = If(Status = StatusCodes.Online,
                                   If(respondingAddress IsNot Nothing, respondingAddress.ToString(), IP),
                                   If(message, String.Empty))
        RaiseEvent StatusChange(Name, Status, address)
    End Sub

    Private Sub RunWorkerCompleted(sender As Object, e As RunWorkerCompletedEventArgs) Handles _backgroundWorker.RunWorkerCompleted
        ResetStatus()
        If _monitoringRequested Then Run()
    End Sub

    Private Sub ResetStatus()
        _pendingStatus = StatusCodes.Unknown
        _pendingAddress = Nothing
        Reply = Nothing
        If Status = StatusCodes.Unknown Then Return
        Status = StatusCodes.Unknown
        RaiseEvent StatusChange(Name, Status, String.Empty)
    End Sub

    Private Function CompareMac(mac1 As String, mac2 As String) As Int32

        Dim _mac1 As String = Replace(mac1, ":", String.Empty)
        _mac1 = Replace(_mac1, "-", String.Empty)

        Dim _mac2 As String = Replace(mac2, ":", String.Empty)
        _mac2 = Replace(_mac2, "-", String.Empty)

        Return StrComp(_mac1, _mac2, CompareMethod.Text)

    End Function

End Class

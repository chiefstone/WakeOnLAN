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

Public NotInheritable Class AboutBox

    Private Sub AboutBox_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ProductTitleLabel.Text = My.Application.Info.Title
        VersionLabel.Text = "版本 " & My.Application.Info.Version.ToString()
    End Sub

End Class

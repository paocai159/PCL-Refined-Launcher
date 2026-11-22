''' <summary>
''' 联机模块的配置来源。
''' 官方版从 PCL 服务器拉取 ServerConfig；开源版没有这个服务器，
''' 因此改为读取 PCL\LinkConfig.json，读不到就用内置的默认配置（走 EasyTier 公共节点）。
''' </summary>
Public Module ModLink

    ''' <summary>本地联机配置文件名（放在 PCL 数据文件夹下）。</summary>
    Public Const ConfigFileName As String = "LinkConfig.json"

    ''' <summary>
    ''' 取联机配置：优先读 PCL\LinkConfig.json 的 Link 节点，否则用内置默认配置。
    ''' </summary>
    Public Function GetLinkConfig() As JObject
        Dim path = Paths.BaseThenName & ConfigFileName
        Try
            If IO.File.Exists(path) Then
                Dim obj = JObject.Parse(IO.File.ReadAllText(path, Text.Encoding.UTF8))
                If obj("Link") IsNot Nothing Then
                    Logger.Info("联机：使用本地配置 " & path)
                    Return obj
                End If
                Logger.Warn("联机：本地配置缺少 Link 节点，改用内置默认配置")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "联机：读取 " & path & " 失败，改用内置默认配置")
        End Try
        Return DefaultLinkConfig()
    End Function

    ''' <summary>内置默认配置：EasyTier 官方 Release + 公共发现节点。</summary>
    Public Function DefaultLinkConfig() As JObject
        Dim cfg = New JObject()
        Dim link = New JObject()
        link("EasyTierVersion") = 1
        link("Downloads") = New JArray(
            "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-{arch}-v2.6.4.zip",
            "https://ghproxy.net/https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-{arch}-v2.6.4.zip",
            "https://gh-proxy.com/https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-{arch}-v2.6.4.zip")
        link("Argument") = ""
        link("DiscoverPeerId") = 94
        link("DiscoverPeer") = "tcp://mc1.easytier.cn:55558"
        link("PeersBlackList") = New JArray()
        link("RandomPeer") = 2
        link("Peers") = New JArray()
        link("MandatoryPeers") = New JArray()
        cfg("Link") = link
        Return cfg
    End Function

    ''' <summary>把内置默认配置写成模板文件，方便用户自己改。</summary>
    Public Function WriteTemplateIfMissing() As String
        Dim path = Paths.BaseThenName & ConfigFileName
        Try
            If IO.File.Exists(path) Then Return path
            IO.File.WriteAllText(path, DefaultLinkConfig().ToString(), Text.Encoding.UTF8)
            Logger.Info("联机：已生成配置模板 " & path)
        Catch ex As Exception
            Logger.Warn(ex, "联机：生成配置模板失败")
        End Try
        Return path
    End Function

End Module

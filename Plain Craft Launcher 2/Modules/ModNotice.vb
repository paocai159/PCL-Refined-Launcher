''' <summary>
''' 主页公告栏的内容。
''' 需要改公告时只改这里，不用动界面代码。
''' </summary>
Public Module ModNotice

    ''' <summary>公告标题（启动器名字）。</summary>
    Public Const NoticeTitle As String = "PCL Refined Launcher"

    ''' <summary>标题后面的蓝色椭圆徽标文字，留空则不显示。</summary>
    Public Const NoticeBadge As String = "新功能"

    ''' <summary>是否在「公告」标题后面显示橙色的 NEW 标记。</summary>
    Public Const NoticeIsNew As Boolean = True

    ''' <summary>公告正文（每行一条，行首可以自己加符号）。</summary>
    Public ReadOnly NoticeLines As String() = {
        "新增 启动器自动检查更新，下载后自动解压替换并重启",
        "新增 恢复内置联机模块：设置 → 联机 即可创建/加入房间",
        "新增 主页公告栏，重要通知以后都会显示在这里",
        "修复 标题栏 RL 角标在子页面与标题文字重叠的问题",
        "目前仅支持离线登录",
        "CurseForge API 调用：暂未申请 API Key",
        "正版登录：暂未向微软申请 Client ID",
        "主题切换：需要赞助解锁的纪念性质功能",
        "当前为正式版 1.0.5.0，问题请到 GitHub 仓库反馈"
    }

    ''' <summary>拼接成一段可直接显示的文本。</summary>
    Public Function GetNoticeText() As String
        Dim sb As New Text.StringBuilder()
        For i = 0 To NoticeLines.Length - 1
            If i > 0 Then sb.Append(vbCrLf)
            sb.Append("· ").Append(NoticeLines(i))
        Next
        Return sb.ToString()
    End Function

End Module

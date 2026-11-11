# HPCPeek 中文快速上手

界面默认为英语，下面使用实际按钮名；作业名和服务器提示保留原文。v1.0.0 支持 Aire 与 Calder。

## 解压与连接

Windows 将完整 `HPCPeek-v1.0.0-win-x64-portable.zip` 解压到可写目录，双击 `HPCPeek.exe`。macOS 15+ 按芯片选择 `osx-arm64` 或 `osx-x64` ZIP，保留完整 `HPCPeek.app`。Mac 文件名带 `unsigned-test` 时为未签名、未公证测试包，不表示已通过 Gatekeeper；不要关闭系统安全机制。

每次正常启动在屏幕中央打开连接表单，右上角可直接选择 **Aire / Calder**，不必先退出向导再去设置。已保存字段预填且全部可编辑。点击 **Connect** 才发起认证；对话框不改变悬浮窗位置，成功后回到紧凑窗。Aire 与 Calder 独立保存地址、网关和账号；之后也可到 **Settings → Connection → Cluster → Edit connection…** 修改。无需退出程序或重启电脑。

从已有 MobaXterm 会话读取参数：

1. 右键会话，打开 **Edit session → SSH**；取 **Remote host / Specify username / Port**，填写相应集群的 Host / Username / Port。
2. 若 **Network settings → SSH gateway (jump host)** 已启用，选择 **SSH gateway**，填写网关的主机、端口、用户名；否则选 **Direct**。
3. 直连前接入学校允许的校园网络 / VPN。不要把 `ssh` 命令、引号或 MobaXterm 的本机转发端口当作真实服务器地址。

默认地址采用用户本次提供的 Aire `aire.leeds.ac.uk:22`、Calder `calder.leeds.ac.uk:22`、SSH 网关 `rash.leeds.ac.uk:22`，已有保存值优先，仍可按学校已确认会话修改。公开登录说明未独立验证这些具体地址；学校网络与登录要求见 [Leeds 登录说明](https://arcdocs.leeds.ac.uk/calder/getting_started/logging_on.html)。无需导出整个 MobaXterm 会话或密码；**From MobaXterm ›** 只展开查找 / 复制参数提示，不读取保存密码。

## 认证与切换

首次连接分别核对网关、目标机的真实主机名、端口及 SHA-256 指纹，请向学校 IT / HPC 通过可信渠道确认。指纹变化会停止连接，不自动接受。随后逐轮响应密码 / keyboard-interactive；阅读服务器 Duo 选项，并在 Duo Mobile 批准，轮数和编号由服务器决定。

服务器要求“普通密码成功后再做 Duo”时，应用在同一连接内继续验证。一次 Duo 批准可能只完成网关；目标机仍可能要求自己的验证。若服务器拒绝额外验证，提示会指出对应主机，不把它误报为已共享密码错误，也不删除有效保存密码。

同一集群中，网关与目标账号名相同且确实使用同一密码时，可主动勾选 **Use one password for both hosts**。普通密码只在本次连接向目标复用一次；Duo、OTP、多字段和语义不明提示仍单独询问。失败会停止并等待主动重连。此选项只复用本次网关 → 目标普通密码，不复用 Duo 回答。

**Password storage** 默认 **This connection**：密码只在本次认证内存中使用。主动选 **Remember on this device** 才将明确普通密码保存到 Windows Credential Manager / macOS Keychain，不进入便携目录、配置导出或日志；Duo、OTP、验证码和模糊提示永不保存。分别在网关 / 目标认证成功后保存该端点，按主机、端口、用户名区分；同一网关账号可共用一个保存项，两个目标机的密码不会混用。下一次仍需主动 Connect，Duo 是否出现由服务器决定，应用不会记住或跳过它。

删除时在 **Settings → Connection** 选择集群，点 **Forget saved passwords**。这会删除该配置的目标和网关保存项并关闭其记住选项，保留当前连接。若两个集群共用同一网关账号，另一边下次也可能重新要求网关密码；更换过的旧端点可在系统凭据管理器中删除其 HPCPeek 项。

认证完成即可关闭 MobaXterm。详情中的集群与型号切换使用该集群已有会话，自动查询不等于重新登录。尚未连接的集群先显示无数据 / 未连接，需主动 Connect。两个集群有独立作业、GPU 采样时间和通知；一边断线不会删掉另一边快照。VPN 中断 / 休眠后保留旧数据，主动 **Reconnect…** 才开始新认证。

## 看窗口、节点与作业

紧凑窗 **340 × 148 DIP**，同时展示 Aire 和 Calder 的节点小图；展开后以 **Cluster / GPU model** 选择集群与型号。展开保持 340 DIP 宽、最高 720 DIP，按当前屏幕工作区缩减。新版没有 `Need` 或每节点需求下拉框，直接看每台节点的剩余整卡。紧凑图只用颜色，悬停有完整说明；展开图显示真实节点名、剩余数字、不可用 `×` 和未知 `?`，可点选查看配置、已分配和状态。选择节点不会让文字移位。

- 绿色连接点表示已登录当前选中的集群；红色表示该集群未登录。悬停查看当前用户与两边状态；Demo 不是账号登录。Running / Queued 汇总已采样集群；底部 free / up to …/node 仅对应当前所选集群 / 型号，详情另列实际 GPUs/node 配置。
- **Jobs… · GPUs…** 分别显示已采样集群中最旧的成功采样年龄，悬停有各集群时间；旧快照或没有采样不能当成当前空闲状态。
- 排队作业优先看等待原因；运行作业显示用时 / 时限和节点。展开后用时按秒更新，本地推算带 `~`，下一次采样校准；断线或样本过期时冻结为最后采样值。此计时不会每秒执行 Slurm 命令；排队、完成中或结束状态不继续推算。长名称可悬停，数组任务 ID / 范围保留。右键 **View details / Copy job ID / Copy full name**。
- 有至少一张空闲整卡的节点统一显示绿色，数字仍保留；不再分浅绿 / 深绿，也没有 3+ 色阶。一个节点有 2 张余卡与两个节点各 1 张不同。`Full / 0` 表示无剩余；`Unavailable / ×` 不可用于候选；`Unknown / ?` 表示字段不足，不是空闲。
- 作业离开队列后按需等待 `sacct` 确认，暂不可见时保留结果待确认。通知注明所属集群，首次连接和恢复不补发旧开始通知。

[Aire / Calder 公开规格](https://arcdocs.leeds.ac.uk/calder/system/node_types.html) 分别为 28 台、每台 3 张 L40S 与 7 台、每台 8 张 H200 NVL；这些只作硬件背景。Calder 的物理上限 8 不等于当前每节点都有 8 张可用：如果当前最多只剩 3，单节点最多可用会显示 3。下拉框不会因旧偏好带入未被该集群数据证实的 L40 型号。实际统计使用当前账号可见的 Slurm 数据，未知字段和不可用节点不能直接从硬件总量推算为空闲。

## 刷新与日常操作

队列和 GPU 默认每 5 分钟采样，**Settings → Connection → Auto refresh** 可选 5 / 15 / 30 / 60 分钟或自定义 15–86,400 秒。保存改变真实查询调度，不重新认证；手动刷新有冷却，同类请求不并发。已有 SSH 30 秒 keepalive 是保活，不是 Slurm 刷新，也不能保证 VPN 或服务器永不断线。预约、优先级、CPU 和内存仍影响实验启动。

拖动顶栏移动；图钉切换固定悬浮 / 临时查看。临时面板失焦隐藏；Windows 托盘或 Mac 菜单栏图标唤回。隐藏期间继续采样，**More → Quit HPCPeek** 才关闭自身连接并退出。More 提供刷新 / 重连、**Settings…** 和退出。

设置分 **Connection / Appearance / Notifications**，主题默认 System，也可选 Light / Dark。通知为可关闭、不抢焦点的应用桌面浮层，8 秒消失；不进入系统通知中心。Windows 用 Ctrl、Mac 用 Command：R 刷新，逗号设置，Q 退出；Escape 先关菜单，再收起 / 隐藏。Tab 可遍历。

## 配置、升级与离线 Demo

Windows 配置仍在 EXE 旁 `portable-data`。升级时先退出应用，解压新包到新目录，再迁移自己的配置目录；不要将配置随包分享。Mac 使用 `~/Library/Application Support/HPCPeek`，移动完整 `.app` 不修改配置。

Mac 新配置不存在时依次读取旧 `AirePeek/settings.json`、`AIRE Peek/settings.json`（均在 Application Support），复制有效配置并保留旧文件与指纹。新目录已有配置优先，损坏文件也不被旧文件覆盖。旧版单集群连接迁移到 Aire，新连接使用用户提供的可编辑默认地址。

**Settings → Appearance → Export… / Import…** 只迁移外观、行为、集群选择与型号等非敏感偏好，排除账号、主机、指纹、密码和验证码，不修改现有主机信任。真实账号验收见 [操作清单](REAL-AIRE-VALIDATION.zh-CN.md)。

离线演示仅有开发入口 `HPCPeek.exe --demo`；登录和设置不提供 Demo 按钮。窗口显著显示 **DEMO**，使用真实界面和同一 Slurm 数据处理逻辑，但快照与认证提示是模拟数据，不能替代学校实测。从 Demo 开始真实连接时会先清空两个集群的模拟快照，只认证选中的集群，另一边保持未连接并保留其配置，避免真假数据相加。

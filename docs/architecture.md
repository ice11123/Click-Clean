# 架构与兼容性

2.6.0 展示层采用固定侧栏与可响应的小窗口布局。MemoryGauge基于实际占比绘制，不使用动画补间；FrostedMaterial缓存64×64颗粒层，Themes/Controls.xaml中的AcrylicOption统一圆角、悬停、按压及键盘焦点。ThemePalette负责染色、高光与高对比降级。截图以WPF内容区尺寸绘制，避免非客户区高度被误填成底部空背景。没有新增依赖或更改设置schema。

MainWindow、MiniIsland 与托盘快捷动作通过 RoutineCleanupPolicy 统一执行原版三步。手动点击不检查占用门槛，自动触发才按配置阈值（新默认55%）判断；未知或无效快照安全跳过。手动高级操作经显式选择及警告确认后进入 MemoryEngine。引擎原子锁覆盖调用与复测，步骤失败即停，权限恢复后异步复测，观测错误与调用错误分开。AutomationPolicy 负责周期、持续、迟滞和冷却；设置 schema 4 以 AutomationNeedsConfirmation 暂停旧已启用自动规则，待用户核对阈值并确认三步，不静默覆盖已有参数。FullCleanup 兼容旧字段但固定为三步。

Core 不依赖 WPF：包含系统 API、引擎、结果、自动策略、设置及存储。App 负责 WPF、托盘、计划任务和 Velopack 集成。Tests 模拟 API 和时间；Diagnostics 独立提供无副作用界面预览与明确选择的真实系统检查。

2.4.0新增MemoryObservations（后台只读持续观测）和ProcessGrowthTracker（进程身份与基线差值）。结果以Id关联，Store.ReplaceObservation只更新现有记录，清空后的迟到样本不得重新插入；主窗口取消或替换观测，30秒观测不持有MemoryEngine运行门闩。主展示使用FollowUp变化而非即时变化。

AutomationEnvironmentReader通过Windows低内存事件、跨语言PDH磁盘计数及前台窗口覆盖显示器的情况提供上下文，后台采样不阻塞WPF线程。AutomationPolicy负责场景避让与单个待执行定时周期，RoutineCleanup在实际执行前重新核对全屏情况，场景变化调用DeferAttempt而非记作成功或失败。默认保持百分比55与原版三步；系统压力模式单独启用。

引擎按命令 2、3、4 调用 `NtSetSystemInformation(80, command)`，临时启用特权并恢复。该接口属于兼容性敏感的原生系统接口，不能保证未来 Windows 行为不变。每步检查返回码，失败后停止，取消仅影响未来步骤。

自动策略使用 TimeProvider 的单调时间戳计算持续时间。采样间隔超过两分钟视为恢复或采样空白，重置超限与周期，不补跑。阈值有五个百分点迟滞，所有整理进入冷却。重启遇到近期清理，重新等待完整冷却，避免密集触发。

Store 的数据目录与安装目录分离。设置范围校验，历史逐条容错、最近 30 条，临时文件替换保存；不读取或删除任意外部路径。旧清栖设置与历史复制后验证，原始数据保留。

更新框架必须先在程序 Main 初始化一次。正式构建绑定官方仓库，稳定渠道为 `win`，使用 GitHub Releases 的 `releases.win.json` 与包资产。IApplicationUpdates 区分 CheckAsync、DownloadAsync、ApplyAsync，自动路径只检查，下载和应用仅由点击入口触发。首页 UpdatePresentation 映射状态与按钮，旧自动下载/应用字段强制关闭。禁用自动启动替换，无整理或复测时才显式应用；不用可能超时杀进程的等待 API。诊断注入模拟服务验收点击行为，不访问真实更新服务。

Inno Setup 首次安装包装 Velopack 便携目录，`current` 是可替换程序目录，根目录包含 Update.exe、安装卸载入口。后续普通更新只替换运行内容，用户数据与 Inno 卸载入口保留。

应用保存已下载包的预期 SHA-256 与尺寸，重新启动后恢复待更新状态；应用前再次校验缓存，防止仅凭文件存在就将损坏包视为可用。记录不构成发布者签名。安装版启动会校验自身路径与安装登记一致，再同步卸载界面的版本号；便携实例不会修改其他安装的版本号。安装与卸载用专用互斥量要求正常退出，不强制结束整理调用。

UpdateService通过IUpdateTransport对接Velopack，测试使用故障传输注入，覆盖实际服务的超时、取消、进度、磁盘不足、重试和二次校验。检查WaitAsync具有30秒上限（底层不支持取消的HTTP检查可能继续完成，但不更新UI）；下载使用原生CancellationToken，15分钟上限。UpdateRecovery记录启动次数、备份配置，正常初始化并进入Dispatcher空闲才确认健康；异常恢复为发行历史入口，不是自动回滚系统。

进程排行按(PID,启动UTC时间)识别进程，只读取私有内存与工作集，界面只显示前12个私有增长排序。页面关闭/隐藏即停采样，不结束进程，不上传或持久化进程名。

安全边界详见 SECURITY.md。当前没有独立签名更新元数据与 Authenticode 证书，不能将同源哈希描述成发布者身份验证。未来若改变框架、架构或不兼容数据格式，应走明确的特殊升级流程并验证恢复。

## 2.5.0 展示层重构

MainWindow保留原生窗口外壳、导航、焦点与系统事件，页面由Views下的OverviewView、HistoryView、DiagnosticsView、SettingsView和ResultDetailsView组成。PresentationState中的可绑定状态负责读数、规则草稿、列表和提示，控件样式集中于Themes/Controls.xaml，ThemePalette统一资源映射和高对比。原始设置schema仍为4，展示状态不序列化到用户配置。

CleanupCoordinator持有同一MemoryEngine、执行取消、步骤反馈与30秒观测生命周期。首页、托盘、岛从统一入口进入；自动在实际执行前核对场景，手动不受占用门槛限制。取消不能中断原生调用，超时继续跟踪。SamplingCoordinator统一整机与自动上下文读数，可见主窗口/浮出岛1秒，其余10秒。ProcessObservation单独拥有可见诊断页的5秒采样，页面代次阻止迟到写入。

导航为概览、记录、诊断三个同级页面和独立设置。设置分类索引不禁用主导航，自动草稿未保存时才处理保存/放弃/继续编辑。结果详情、自定义步骤和进程辅助信息使用WPF Popup；Escape只关闭临时面板，焦点回到来源。主导航即时切换，真实读数不作装饰动画。

ResultRow统一首页和记录的详情含义，优先3秒复测、独立30秒观测，允许未知、零和负值。观测写盘失败不改变系统步骤成功，不显示虚假持续观测。进程行以PID/启动时间识别，数字更新而非逐个重建视觉树，操作时暂缓重排。

| Before | After | Why |
| --- | --- | --- |
| 设置/记录占满覆盖层，诊断归属设置 | 固定同级导航，设置浅层分类 | 当前位置清楚，相关页面直接可达 |
| 主窗口集中了系统执行与多类采样 | CleanupCoordinator、SamplingCoordinator、ProcessObservation | 任务生命周期不依赖某个页面是否显示 |
| 代码拼接历史与进程控件 | 绑定集合与XAML模板 | 统一主题、排版、焦点和行身份 |
| 多类消息共用StatusText | 内存、整理、自动、更新、设置反馈分开 | 避免普通读数刷新覆盖执行状态 |
| 深色显式标题样式可能回落到黑色 | 标题基于共享正文样式并做实际颜色断言 | 视觉审查和测试同时约束可见性 |
| 自动表单全展开、编辑和生效混在一起 | 参数渐进展开、规则草稿明确保存 | 减少信息负担，保持执行规则可预期 |

动效按emil-design-eng和animate原则，用现有IslandMotion及WPF原生能力：鼠标按压120ms、轻量浮层180ms、岛150ms浮出，键盘即时响应。岛呼吸2.8秒、工作背景小范围移动、慢循环30帧；减少动画/高对比/收起停止时钟。没有引入WebView、UI或动效第三方框架。WPF透明窗口的实际开销以测量为准，不套用CSS合成性能保证。

由于维护者搞坏了`已知游戏版本支持`项，兼容性请以下方为准:
Due to a maintainer mishandling of the `Known Game Version Support` settings, please refer to the following compatibility guidelines:
兼容性: [b]正式版与 beta版(v0.111.0)[/b]
Compatibility: [b]stable and  public-beta-v0.111.0[/b]

遇到问题请在讨论区留言。Please leave a message in the discussion area if you encounter any problems.

为多人模式增加更多的对抗性！（bushi）

这个mod将争抢遗物的随机猜拳替换为带有倒计时的手动出拳，玩家可以自行选择要出石头剪刀还是布，在倒计时结束后未选择则会仍然随机出拳。

A Slay the Spire 2 mod that replaces the random rock-paper-scissors used when multiple players contest the same treasure relic with player-chosen gestures on a countdown, if no hand is chosen by the end of the countdown, the hand will still be randomly generated.

倒计时时长可以在主机端的RitsuLib设置页面修改。

Duration of countdown can be sat in Host‘s RitsuLib's settings page.

可以在主机端的RitsuLib设置页面激活可重选手势选项，在可重选激活手势后，结算将仅在倒计时结束后进行（未激活时在所有玩家都选择了手势时也会进行结算），在倒计时期间，玩家可以点击所选手势正下方的复位按钮以重选。

The reselectable gesture option can be activated in the RitsuLib settings page on the console. After the reselectable gesture is activated, the results will only be calculated after the countdown ends (if it is not activated, the results will also be calculated when all players have selected a gesture). During the countdown, players can click the reset button directly below the selected gesture to reselect.

自v0.2.0版本开始，[b]路径选择冲突[/b]与[b]投票事件的冲突[/b]也将支持由轮盘赌（Host抽选）变为石头剪刀布，采用获胜边制（只要一方被完全淘汰出石头剪刀布对局对局就会结束并应用另一方的选择）。这是一个仅主机开启有效的选项，未避免烦人，其被设置为默认关闭。

Starting with version 0.2.0, [b]path selection conflicts[/b] and [b]voting event conflicts[/b] will support changing from roulette (host selection) to rock-paper-scissors, using a winning side system (the game ends and the other side's choice applies once one side is completely eliminated from the rock-paper-scissors match). This is an option only enabled by the host and is disabled by default to avoid being annoying.

同时，自v0.2.0版本开始，一个更适合多人的石头剪刀布动画被实现，这个动画动态计算每只手的位置，避免了>4人联机时手部重叠的问题。目前此动画只在路径选择冲突或者投票事件的冲突猜拳中出现。

Also, starting with version 0.2.0, a more suitable rock-paper-scissors animation for multiple players has been implemented. This animation dynamically calculates the position of each hand, avoiding hand overlap issues when playing with more than 4 players. Currently, this animation only appears in path selection conflicts or voting event conflicts.


[h2] 修改日志 Change Logs [/h2]


[h3]v0.2.0[/h3]

[b]经过测试模组在正式版与beta分支(v0.111.0)可以运行[/b]，本版本的主要修改如下：
[b]After testing, the module can run in both the official version and the beta branch (v0.111.0).[/b] The main changes in this version are as follows:
[olist]
 [*] 新增：选路与共享事件的投票分歧不再使用轮盘赌，改为手动石头剪刀布，并按获胜方所投的路径/选项结算（两个功能可在主机端分别开关）。
 [*] 更好的适合多人的猜拳动画：动态计算手的角度，在大于四人时手不会重叠。
 [*] 设置页支持中英文，随游戏语言自动切换。
[/olist]

[b]The mod has been tested and works on the beta branch[/b]. The main changes in this version are as follows:

[olist]

[*] New: split votes when choosing a path or a shared event option no longer use the roulette; a manual rock-paper-scissors decides by the winning side's coordinate/option (both replacements can be toggled separately on the host side).
[*] Improved animation for multiplayer rock-paper-scissors: Dynamically calculates hand angles to prevent hand overlap when there are more than four players.
[*] Settings page now supports both Chinese and English, automatically switching with the game language.
[/olist]

[h3]v0.1.1[/h3]

[b]经过测试模组在正式版与beta分支(v0.108.0)可以运行[/b]，本版本的主要修改如下：
[b]After testing, the module can run in both the official version and the beta branch (v0.108.0).[/b] The main changes in this version are as follows:
[olist]
 [*] 修复了在猜拳进行中时打开pause菜单的错误UI层级
 [*] 添加了可选的重选手势选项，在主机端激活重选手势选项时，结算只会在倒计时结束后进行（目前结算会在倒计时结束以及所有玩家都选择手势后进行），在倒计时期间可以更改自己选择的手势。
 [*] 修复了正式版中的原版bug：当某位玩家在猜拳过程中打开暂停/ESC菜单时，其缩回屏幕外的手不会再回来（要到抓取遗物时才恢复）。此修复默认关闭，因为beta分支V0.108.0已修复，正式版可在 RitsuLib设置中开启“修复暂停后消失的手“，这个选项是本地选项。
[/olist]

[b]The mod has been tested and works on the beta branch[/b]. The main changes in this version are as follows:

[olist]

[*] Fixed a bug in the UI hierarchy where the pause menu would open during rock-paper-scissors.
[*] Added an optional reselect gesture option. When a reselect gesture option is activated on the host side, the results will only occur after the countdown ends (currently, the results occur after the countdown ends and all players have selected their gestures). Players can change their selected gesture during the countdown.
[*] Fixed a stable-branch bug: when a player opened the pause/ESC menu during rock-paper-scissors, their hand retracted off-screen and never returned (it only came back at the relic grab). This fix is off by default, since the beta branch(v0.108.0) already fixes it, so it can be turned on via the "Fix pause-hidden hands" in  RitsuLib's setting, the setting option is a local option.
[/olist]

All source code and images are released on GitHub under the MIT license. Art assets from Megacrit used in the project are still subject to Megacrit's usage rules.
https://github.com/zentialEdwardSu/STS2-BetterRockPaperScissors

兼容性: [b]正式版与 beta版(v0.111.0)[/b]
Compatibility: [b]stable and public-beta-v0.111.0[/b]

为多人模式增加更多的对抗性！（bushi）

这个mod把手动出拳从“抢遗物”扩展到多人模式里所有需要随机裁决的“投票冲突”：猜拳不再由系统随机生成，改由玩家自己在倒计时内出拳决定。

[b]它替换了三处随机裁决：[/b]
[olist]
[*] 多人争抢同一件遗物 → 带倒计时的手动出拳（原mod的功能）
[*] 选地图路径时投票不一致 → 不再用轮盘赌随机点一个人，改由手动石头剪刀布决定走哪条路
[*] 共享事件里选项投票不一致 → 同样改由手动石头剪刀布决定选哪个选项
[/olist]

两处新增的冲突替换可以在[b]主机端[/b]的 RitsuLib 设置页里分别开关，默认开启；关闭后恢复原版轮盘赌。

[b]怎么打：[/b]按“边”淘汰。一轮里恰好出现两种手势时，被克制的一方出局；只要剩下的玩家都属于同一个选项/同一条路径，这一边就获胜并立刻结束——不会为了让同侧玩家分出一个“最后的人”而反复猜拳。

[b]表现：[/b]选拳时每位玩家的手指手从屏幕边缘伸入、指向屏幕中心，选择面板叠在手的上方；出拳时手臂绕臂根连摇三次（0.5s / 0.4s / 0.2s，越来越快），摇完变成实际出的手势，猜错的手抖动后消失。地图冲突结束后，获胜侧所有玩家的头像放大、其余淡出约 1 秒，随后直接进入获胜侧所投的房间；原版轮盘赌动画不会再出现（共享事件冲突同理，结束后直接高亮被选中的选项、其余置灰）。

倒计时时长、以及“可重选手势”选项，都在主机端的 RitsuLib 设置页修改，对遗物出拳与冲突出拳同时生效；设置页支持中英文，会跟随游戏语言切换。

A Slay the Spire 2 mod that extends manual gestures from relic fights to every multiplayer “vote conflict” that the game would otherwise settle at random.

[b]It replaces three random rolls:[/b]
[olist]
[*] Multiple players contesting the same treasure relic → manual gestures on a countdown (the mod's original feature)
[*] Players voting for different map coordinates → manual rock-paper-scissors decides which path the party takes, instead of the roulette
[*] Players voting for different options in a shared event → manual rock-paper-scissors decides which option runs
[/olist]

The two new conflict replacements are toggled separately in the [b]host's[/b] RitsuLib settings page and are on by default; turning one off restores the vanilla roulette for it.

[b]How a fight works:[/b] elimination by side. A round only eliminates anyone when exactly two distinct gestures are present — everyone who threw the beaten gesture is out. As soon as every remaining player is on the same coordinate/option, that side wins and the fight ends, so players on the winning side never have to play each other just to produce a single “last one standing”.

[b]Presentation:[/b] while choosing, each player's pointing hand reaches in from the edge of the screen towards the centre and the picker panel sits on top of the hands; when the round resolves, each arm swings three times around its root (0.5s / 0.4s / 0.2s, accelerating) and then switches to the gesture it played, while the hands that lost shake and fade away. After a map conflict the winning side's HUD avatars grow and the others fade for about a second, then the party travels to the winning side's room — the vanilla roulette is never replayed (same for shared events, where the winning option is highlighted and the others grey out).

The countdown length and the “re-choose” option live in the host's RitsuLib settings page and apply to both relic fights and conflict fights. The settings page is available in English and Simplified Chinese and follows the game's language.

[h2] 修改日志 Change Logs [/h2]

[h3]v0.2.0[/h3]

[b]经过测试模组在正式版与beta分支(v0.111.0)可以运行[/b]，本版本的主要修改如下：
[b]After testing, the mod works on both the stable version and the beta branch (v0.111.0).[/b] The main changes in this version are as follows:

[olist]
[*] 新增：多人模式的地图路径投票分歧、共享事件选项投票分歧不再使用轮盘赌，改由手动石头剪刀布按“获胜边”决定（两个功能可在主机端 RitsuLib 设置页分别开关，默认开启）
[*] 新增：胜负判定改为“获胜边”——所有存活玩家同属一侧即刻结束，不再为了选出唯一胜者而让同侧玩家继续对局
[*] 新增：出拳动画。选拳时显示遗物式的手指手并从屏幕边缘伸入，选择面板叠在手的上方；结算时手臂绕臂根加速摇动三次（0.5s / 0.4s / 0.2s）后切换为实际手势，本轮被淘汰的手抖动后淡出
[*] 新增：地图冲突结束的揭示动画——获胜侧所有玩家头像放大、其余淡出，然后进入获胜侧所投房间；并且[b]彻底不再播放原版轮盘赌动画[/b]（共享事件冲突同样不再播放，结束后保留原版“被选选项高亮 + 其余置灰”）
[*] 新增：设置页中英文本地化，跟随游戏语言自动切换（RitsuLib I18N，翻译表内嵌于 DLL）
[*] 兼容性：保持同一份 DLL 同时支持正式版与 beta 分支
[/olist]

[b]The mod works on both branches (v0.111.0).[/b] The main changes in this version are as follows:

[olist]
[*] New: split votes on the map and in shared events no longer use the roulette — a manual rock-paper-scissors fight decides the winning side (each replacement can be toggled separately in the host's RitsuLib settings, on by default).
[*] New: the fight is decided by the winning side. The moment every remaining player is on the same side it ends, so players on the same side never keep fighting each other just to crown a single winner.
[*] New: throw animation. While choosing, the relic-style pointing hands reach in from the screen edges with the picker on top; on resolution each arm swings three times around its root (0.5s / 0.4s / 0.2s, accelerating) and then switches to the played gesture, while the hands eliminated this round shake and fade out.
[*] New: after a map conflict the winning side's avatars grow and the others fade, then the party travels to the winning side's room; the vanilla roulette is [b]never replayed[/b] (same for shared events, which keep the vanilla “chosen option highlighted, others greyed out” ending).
[*] New: the settings page is localized in English and Simplified Chinese and follows the game's language (RitsuLib I18N, tables embedded in the DLL).
[*] Compatibility: the same DLL keeps working on both the stable version and the beta branch.
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

# Voice Input

为 Beat Saber 键盘提供语音输入的模组，面向游戏版本 `1.45.2_28848`。

依赖 BSIPA `^4.3.7`、BeatSaberMarkupLanguage `^1.14.1`、SiraUtil `^3.3.1` 和 [LocalModUI](https://github.com/StarsWhere/LocalModUI) `^0.1.0`。LocalModUI 需要单独安装。

使用已安装相应依赖的游戏目录与 .NET 8 SDK 构建：

```powershell
git clone --recurse-submodules https://github.com/StarsWhere/VoiceInput
cd VoiceInput
pwsh -File scripts/build_release.ps1 -GameRoot 'C:\Games\Beat Saber'
```

此版本使用本地移植的依赖组合，独立仓库发布未追加游戏或 VR 测试。源代码使用根目录的 MIT 许可证，第三方许可信息见 `THIRD-PARTY-NOTICES.txt`。

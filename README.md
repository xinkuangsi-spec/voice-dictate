# voice-dictate

Windows 上的本地语音听写：按住鼠标侧键说话，松开后识别结果直接以键盘输入的方式打进当前窗口。全程本地运行，不联网。

## 用法

- **按住鼠标前侧键**说话，松开结束
- 或 **Ctrl+Alt+Space** 开始，再按一次结束
- 录音或识别过程中按 **Esc**（或点一下浮窗）取消
- **退出**：右键任务栏右下角的麦克风托盘图标 →「退出语音输入」，识别服务和显卡上的模型会一起关掉
- 带 `--announce` 参数启动（比如桌面快捷方式）会弹一下"已启动 / 已在运行"的提示；开机自启不带参数，不弹

屏幕底部会出现一个浮窗：说话时显示实时音量，识别时显示扫光提示；麦克风打不开或只录到静音时会直接给出提示，而不是干等。

## 组成

| 文件 | 作用 |
|---|---|
| `stt-server.js` | 常驻的本地识别服务（Node，监听 127.0.0.1:8377）：用 ffmpeg 采集麦克风，交给 llama.cpp 在显卡上跑 Qwen3-ASR 识别 |
| `chinese-itn.js` | 把识别结果里的中文数字转成阿拉伯数字（"十六分钟"→"16分钟"、"百分之三十"→"30%"），成语和"十分重要"这类不动 |
| `dictate.cs` | 客户端源码（WinForms）：全局鼠标钩子、热键、分层窗口浮窗，识别结果用 SendInput 打出去 |
| `build.ps1` | 把 `dictate.cs` 编译成 `voice-dictate.exe` |

客户端启动时会先探测 8377 端口，服务没起就自己拉起来（无窗口）。客户端本身是 GUI 程序，没有控制台窗口。

## 依赖

- Windows 10 / 11
- [Node.js](https://nodejs.org/)
- [ffmpeg](https://ffmpeg.org/)，需要在 `PATH` 里（用它的 dshow 采集麦克风）
- [llama.cpp](https://github.com/ggml-org/llama.cpp/releases) 的 Windows Vulkan 版（`llama-bin-win-vulkan-x64.zip`），AMD / NVIDIA / Intel 显卡都能用
- [ggml-org/Qwen3-ASR-1.7B-GGUF](https://huggingface.co/ggml-org/Qwen3-ASR-1.7B-GGUF) 的 `Qwen3-ASR-1.7B-Q8_0.gguf` 和 `mmproj-Qwen3-ASR-1.7B-Q8_0.gguf`（魔搭上有同名仓库，国内下得快）

llama.cpp 和模型都不在本仓库里，默认路径是 `D:/models/llama.cpp/bin/llama-server.exe` 和 `D:/models/qwen3-asr-1.7b/`，放在别处就在 `config.json` 里改。

显存：识别服务第一次按键时才启动（加载约 4 秒，和录音同时进行），常驻约 1.5GB；闲置 `asrIdleMinutes`（默认 10 分钟）后自动退出，显存全部释放。

## 构建与运行

必须用 **Windows PowerShell 5.1**（PowerShell 7 的 `Add-Type` 不支持 `-OutputAssembly`）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
.\voice-dictate.exe
```

开机自启：在 `shell:startup` 里放一个指向 `voice-dictate.exe` 的快捷方式。

## 配置

同目录下放 `config.json`（可选，所有键都可省略）：

```json
{
  "mouseButton": "side2",
  "llamaServer": "D:/models/llama.cpp/bin/llama-server.exe",
  "asrModel": "D:/models/qwen3-asr-1.7b/Qwen3-ASR-1.7B-Q8_0.gguf",
  "asrMmproj": "D:/models/qwen3-asr-1.7b/mmproj-Qwen3-ASR-1.7B-Q8_0.gguf",
  "asrPort": 8378,
  "asrIdleMinutes": 10,
  "ffmpeg": "ffmpeg",
  "port": 8377
}
```

`mouseButton`：`side2` 前侧键（默认）、`side1` 后侧键、`none` 只用热键。

麦克风不用配置，跟随 Windows 当前的默认录音设备。

## 服务端接口

只监听本机：

| 接口 | 说明 |
|---|---|
| `GET /start?device=<录音端点 GUID>` | 开始录音；麦克风打不开时返回 500 和 ffmpeg 的原始报错 |
| `GET /stop` | 停止并返回识别文本；只录到静音时返回 422 `SILENT` |
| `GET /cancel` | 停止并丢弃 |
| `GET /quit` | 关掉 llama-server 并退出服务（托盘"退出"调用） |
| `GET /level` | 当前输入电平 0–100（按 -60dBFS…0dBFS 映射） |
| `GET /state` / `GET /ping` | 状态 / 存活检查 |

## 致谢

- 识别模型 [Qwen3-ASR](https://huggingface.co/Qwen/Qwen3-ASR-1.7B)，运行时 [llama.cpp](https://github.com/ggml-org/llama.cpp)
- `chinese-itn.js` 移植自 [CapsWriter-Offline](https://github.com/HaujetZhao/CapsWriter-Offline) 的 `chinese_itn.py`（MIT）
- 浮窗的音量条和"识别中"扫光分别移植自 [react-bits](https://github.com/DavidHDev/react-bits) 的 `SlicedWaves` 与 `ShinyText`

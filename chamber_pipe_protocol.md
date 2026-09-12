# Chamber Named Pipe 通讯协议

本文档描述箱体控制程序（`chamber.exe` / `yixing_chamber.exe` / `binder_chamber.exe`）对外暴露的 named pipe（命名管道）接口，供外部进程读取设备状态、设置温湿度、控制启停以及执行温湿度线性控制（ramp）任务。

> **并非所有设备都支持所有命令：命令只有在设备的硬件支持时才会有效。** 本套协议为三个控制程序共用，但各设备可用命令集不同（如 yixing_chamber 无湿度硬件、binder_chamber 不支持启停但支持风机），不受支持的命令返回错误。差异明细见 [第 8 节](#8-设备差异说明chamber--yixing_chamber--binder_chamber)。

---

## 1. 概述

| 项目 | 说明 |
|---|---|
| 传输层 | Windows Named Pipe，字节流模式（`PipeTransmissionMode.Byte`） |
| 编码 | UTF-8 |
| 管道名 | 默认与程序同名：`chamber` / `yixing_chamber` / `binder_chamber`（可在 UI 顶部 "Pipe name" 文本框修改，点击 Apply 生效） |
| 消息格式 | 每行一个 JSON 对象；客户端发送一行请求，服务端返回一行响应 |
| 连接模型 | 一个连接可进行多轮"请求 → 响应"；服务端逐行读取，每读一行处理并回复一行 |

**连接方式（C# 示例）：**

```csharp
using (var pipe = new NamedPipeClientStream(".", "chamber", PipeDirection.InOut))
{
    pipe.Connect();
    using (var writer = new StreamWriter(pipe) { AutoFlush = true })
    using (var reader = new StreamReader(pipe))
    {
        writer.WriteLine("{\"cmd\":\"get_status\"}");   // 发送请求
        string response = reader.ReadLine();             // 读取一行响应
    }
}
```

---

## 2. 请求格式

每个请求是一个 JSON 对象，字段如下：

```json
{"cmd":"<命令>","value":<数字>,"end":<数字>,"duration":<数字>}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `cmd` | string | 是 | 命令名，**大小写不敏感**（`get_status` == `GET_STATUS`） |
| `value` | number | 视命令 | 目标值 / ramp 开始值 |
| `end` | number | 视命令 | ramp 结束值 |
| `duration` | number | 视命令 | ramp 时长（秒，正整数） |

> 请求字段名匹配是大小写不敏感的：`cmd` / `Cmd` / `CMD` 均有效。

---

## 3. 响应格式

所有响应都是同一个对象结构序列化而来，**固定包含 22 个字段，字段名为 PascalCase**（由 `JavaScriptSerializer` 直接序列化 C# 属性名产生）。

**成功响应：**

```json
{"Ok":true, ...}
```

**失败响应：**

```json
{"Ok":false,"Error":"<错误消息>", ...}
```

> 说明：除 `get_status` 外，命令的成功响应其余字段均为默认值（`false` / `0` / `null`）。为保持示例简洁，下文除 `get_status` 外仅展示关键字段 `Ok` 与 `Error`，其余字段省略；实际线上响应会包含全部 22 个字段（结构见 [第 5 节](#5-get_status-响应字段)）。`Error` 字段在成功响应中为 `null`。

---

## 4. 命令详解

### 4.1 `get_status` — 读取完整状态

**请求：**

```json
{"cmd":"get_status"}
```

**响应（成功）：**

```json
{"Ok":true,"Error":null,"HasStatus":true,"CurTemp":32.7,"TgtTemp":27.0,"CurHum":42.5,"TgtHum":50.0,"RunState":1,"TempRampRunning":true,"TempRampStart":25.0,"TempRampEnd":60.0,"TempRampDuration":600,"TempRampElapsed":12,"TempRampRemaining":588,"TempRampTarget":25.7,"HumRampRunning":false,"HumRampStart":30.0,"HumRampEnd":80.0,"HumRampDuration":600,"HumRampElapsed":0,"HumRampRemaining":600,"HumRampTarget":0}
```

字段含义见 [第 5 节](#5-get_status-响应字段)。

---

### 4.2 `set_temp` — 设置目标温度

立即写入目标温度寄存器。**会同时停止正在运行的温度 ramp 任务。**

**请求：**

```json
{"cmd":"set_temp","value":27.5}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 未连接串口 | `{"Ok":false,"Error":"not connected", ...}` |
| 超出范围 | `{"Ok":false,"Error":"Temperature must be between -40.0 and 150.0 °C.", ...}` |

> 有效范围：`-40.0 ~ 150.0`（°C）。

---

### 4.3 `set_humidity` — 设置目标湿度

立即写入目标湿度寄存器。**会同时停止正在运行的湿度 ramp 任务。**

**请求：**

```json
{"cmd":"set_humidity","value":50.0}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 未连接串口 | `{"Ok":false,"Error":"not connected", ...}` |
| 超出范围 | `{"Ok":false,"Error":"Humidity must be between 0.0 and 100.0 %RH.", ...}` |

> 有效范围：`0.0 ~ 100.0`（%RH）。

---

### 4.4 `start` — 启动设备

**请求：**

```json
{"cmd":"start"}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 未连接串口 | `{"Ok":false,"Error":"not connected", ...}` |

---

### 4.5 `stop` — 停止设备

**请求：**

```json
{"cmd":"stop"}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 未连接串口 | `{"Ok":false,"Error":"not connected", ...}` |

---

### 4.6 `start_temp_ramp` — 启动温度线性控制

启动温度 ramp：先把目标温度设为 `value`（开始值），之后每秒按时间匀速线性变化，`duration` 秒后设为 `end`（结束值）并自动停止。

**请求：**

```json
{"cmd":"start_temp_ramp","value":25.0,"end":60.0,"duration":600}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 未连接串口 | `{"Ok":false,"Error":"not connected", ...}` |
| 温度超出范围 | `{"Ok":false,"Error":"Temperature must be between -40.0 and 150.0 °C.", ...}` |
| 时长非法 | `{"Ok":false,"Error":"duration must be a positive integer (seconds)", ...}` |

> 有效范围：`value` / `end` 为 `-40.0 ~ 150.0`；`duration` 为正整数秒。

---

### 4.7 `stop_temp_ramp` — 停止温度线性控制

**请求：**

```json
{"cmd":"stop_temp_ramp"}
```

**响应：**

```json
{"Ok":true,"Error":null, ...}
```

> 停止后目标温度保持最后写入的值，不做额外写入。

---

### 4.8 `start_hum_ramp` — 启动湿度线性控制

启动湿度 ramp：先把目标湿度设为 `value`（开始值），之后每秒按时间匀速线性变化，`duration` 秒后设为 `end`（结束值）并自动停止。

**请求：**

```json
{"cmd":"start_hum_ramp","value":30.0,"end":80.0,"duration":600}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 未连接串口 | `{"Ok":false,"Error":"not connected", ...}` |
| 湿度超出范围 | `{"Ok":false,"Error":"Humidity must be between 0.0 and 100.0 %RH.", ...}` |
| 时长非法 | `{"Ok":false,"Error":"duration must be a positive integer (seconds)", ...}` |

> 有效范围：`value` / `end` 为 `0.0 ~ 100.0`；`duration` 为正整数秒。

---

### 4.9 `stop_hum_ramp` — 停止湿度线性控制

**请求：**

```json
{"cmd":"stop_hum_ramp"}
```

**响应：**

```json
{"Ok":true,"Error":null, ...}
```

> 停止后目标湿度保持最后写入的值，不做额外写入。

---

### 4.10 `set_fan` — 设置风机转速（仅 binder_chamber）

立即写入风机转速设定值。

**请求：**

```json
{"cmd":"set_fan","value":80.0}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 设备未连接 | `{"Ok":false,"Error":"not connected", ...}` |
| 超出范围 | `{"Ok":false,"Error":"Fan speed must be between 30 and 100 %.", ...}` |
| 设备不支持（chamber / yixing_chamber） | `{"Ok":false,"Error":"unknown command: set_fan", ...}` |

> 有效范围：`30.0 ~ 100.0`（%）。

---

### 4.11 未知命令

**请求：**

```json
{"cmd":"foo"}
```

**响应：**

```json
{"Ok":false,"Error":"unknown command: foo", ...}
```

---

### 4.12 无效请求 / 非法 JSON

| 场景 | 示例请求 | 响应 |
|---|---|---|
| 缺少 `cmd` | `{}` 或 `{"value":1}` | `{"Ok":false,"Error":"invalid request", ...}` |
| JSON 语法错误 | `not json` | `{"Ok":false,"Error":"bad request: <解析异常消息>", ...}` |

---

## 5. `get_status` 响应字段

| 字段 | 类型 | 说明 |
|---|---|---|
| `Ok` | bool | 请求是否成功 |
| `Error` | string | 错误消息（成功时为 `null`） |
| `HasStatus` | bool | 是否已从设备读到有效状态（true 表示下列 Cur/Tgt/RunState 有效） |
| `CurTemp` | number | 当前温度（°C） |
| `TgtTemp` | number | 目标温度（°C） |
| `CurHum` | number | 当前湿度（%RH） |
| `TgtHum` | number | 目标湿度（%RH） |
| `RunState` | number | 运行状态：`0` = 停止，`1` = 运行 |
| `TempRampRunning` | bool | 温度 ramp 是否运行中 |
| `TempRampStart` | number | 温度 ramp 开始值 |
| `TempRampEnd` | number | 温度 ramp 结束值 |
| `TempRampDuration` | number | 温度 ramp 时长（秒） |
| `TempRampElapsed` | number | 温度 ramp 已运行时间（秒） |
| `TempRampRemaining` | number | 温度 ramp 剩余时间（秒） |
| `TempRampTarget` | number | 温度 ramp 当前应达到的目标值 |
| `HumRampRunning` | bool | 湿度 ramp 是否运行中 |
| `HumRampStart` | number | 湿度 ramp 开始值 |
| `HumRampEnd` | number | 湿度 ramp 结束值 |
| `HumRampDuration` | number | 湿度 ramp 时长（秒） |
| `HumRampElapsed` | number | 湿度 ramp 已运行时间（秒） |
| `HumRampRemaining` | number | 湿度 ramp 剩余时间（秒） |
| `HumRampTarget` | number | 湿度 ramp 当前应达到的目标值 |
| `FanSp` | number | 风机转速设定值（%）。**仅 binder_chamber 返回**（共 23 字段） |

> 注：ramp 未运行时，`*RampTarget` 为 `0`，`*RampElapsed` 为 `0`，`*RampRemaining` 等于 `*RampDuration`。
> 注：**并非所有设备都返回全部字段**——yixing_chamber 无湿度硬件，响应中没有 `CurHum` / `TgtHum` / `HumRamp*` 字段（共 13 字段）；`FanSp` 仅 binder_chamber 提供。外部程序解析时应先判断字段是否存在。

---

## 6. 错误消息一览

| 错误消息 | 触发条件 |
|---|---|
| `not connected` | 串口未连接时调用 `set_temp` / `set_humidity` / `start` / `stop` / `start_temp_ramp` / `start_hum_ramp` |
| `Temperature must be between -40.0 and 150.0 °C.` | 温度超出范围 |
| `Humidity must be between 0.0 and 100.0 %RH.` | 湿度超出范围 |
| `Fan speed must be between 30 and 100 %.` | 风机转速超出范围（binder_chamber） |
| `duration must be a positive integer (seconds)` | ramp 时长不是正整数 |
| `invalid request` | 请求缺少 `cmd` 或 `cmd` 为空 |
| `unknown command: <命令>` | 未识别的命令，或该设备硬件不支持此命令 |
| `unsupported` | binder_chamber 收到 `start` / `stop`（该设备不支持启停） |
| `bad request: <解析异常消息>` | JSON 无法解析 |

---

## 7. 注意事项

1. **命令并发**：多个命令（UI 按钮、pipe 客户端、每秒轮询）共享同一把串口写锁，串行发送，不会出现帧交叉。每个命令内容完整，先后顺序不影响正确性。
2. **手动设置与 ramp 互斥**：运行温度 ramp 时若收到 `set_temp`，会先自动停止温度 ramp 再写入；湿度同理。温/湿 ramp 相互独立，可并行。
3. **ramp 行为**：启动即写开始值，每秒按线性插值更新（粒度 0.1，仅当值变化才写寄存器），时间到写结束值并置为停止；中途停止则目标保持最后值。
4. **管道实例冲突**：Windows 允许多个进程对同一管道名创建服务器（会互相抢占连接）。chamber 启动管道前会用客户端探针检测冲突，若名称已被占用则拒绝启动并提示。
5. **响应键名**：为 PascalCase（如 `CurTemp`、`TgtTemp`），与请求键名的小写风格不同，外部程序解析时请以 PascalCase 为准。

---

## 8. 设备差异说明（chamber / yixing_chamber / binder_chamber）

三个控制程序共用同一套管道协议，但**可用命令集取决于设备硬件能力——只有硬件支持，命令才有效**；对不支持的命令，设备返回错误（`unknown command` 或 `unsupported`），或响应中不含相关字段。

| 能力 / 命令 | chamber（默认） | yixing_chamber | binder_chamber |
|---|---|---|---|
| 默认管道名 | `chamber` | `yixing_chamber` | `binder_chamber` |
| 设备连接方式 | 串口 Modbus-RTU | 串口 Modbus（变体） | 以太网（UDP 发现 + TCP Modbus） |
| `get_status` | ✅ | ✅ | ✅ |
| `set_temp` / `start_temp_ramp` / `stop_temp_ramp` | ✅ | ✅ | ✅ |
| `set_humidity` / `start_hum_ramp` / `stop_hum_ramp` | ✅ | ❌ 无湿度硬件（返回 `unknown command`，状态中也无 `CurHum`/`TgtHum`/`HumRamp*` 字段） | ✅ |
| `set_fan` | ❌ | ❌ | ✅（`30 ~ 100` %） |
| `start` / `stop` | ✅ | ✅ | ❌ 该机型不支持（返回 `unsupported`，`RunState` 恒为 `0`） |
| `get_status` 响应字段数 | 22 | 13（无湿度字段） | 23（较 chamber 新增 `FanSp`） |

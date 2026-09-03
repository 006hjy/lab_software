# Alicat MFC Named Pipe 通讯协议

本文档描述 `alicat_mfc.exe` 对外暴露的 named pipe（命名管道）接口，供外部进程读取两路质量流量计（MFC）的状态、设置流量设定值，以及配置气体混合参数（源气浓度 / 总流速 / 目标浓度）。

---

## 1. 概述

| 项目 | 说明 |
|---|---|
| 传输层 | Windows Named Pipe，字节流模式（`PipeTransmissionMode.Byte`） |
| 编码 | UTF-8 |
| 管道名 | 默认 `alicat_mfc`（可在 UI 顶部 "Pipe name" 文本框修改，点击 Apply 生效） |
| 消息格式 | 每行一个 JSON 对象；客户端发送一行请求，服务端返回一行响应 |
| 连接模型 | 一个连接可进行多轮"请求 → 响应"；服务端逐行读取，每读一行处理并回复一行 |

**连接方式（C# 示例）：**

```csharp
using (var pipe = new NamedPipeClientStream(".", "alicat_mfc", PipeDirection.InOut))
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
{"cmd":"<命令>","role":"<角色>","value":<数字>}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `cmd` | string | 是 | 命令名，**大小写不敏感**（`get_status` == `GET_STATUS`） |
| `role` | string | 视命令 | 目标角色：`target`（目标气）/ `background`（背景气） |
| `value` | number | 视命令 | 设定值（流速 SCCM 或浓度，均支持小数） |

> 请求字段名匹配是大小写不敏感的：`cmd` / `Cmd` / `CMD` 均有效。

---

## 3. 响应格式

所有响应都是同一对象结构序列化而来，字段名为 **PascalCase**（由 `JavaScriptSerializer` 直接序列化 C# 属性名产生）。

**成功响应：**

```json
{"Ok":true, ...}
```

**失败响应：**

```json
{"Ok":false,"Error":"<错误消息>", ...}
```

> 说明：除 `get_status` 外，命令的成功响应其余字段均为默认值（`false` / `0` / `null`）。`Error` 字段在成功响应中为 `null`。

---

## 4. 命令详解

### 4.1 `get_status` — 读取完整状态

读取两路 MFC 的当前状态，以及气体混合的三个参数。

**请求：**

```json
{"cmd":"get_status"}
```

**响应（成功）：**

```json
{"Ok":true,"Error":null,"Devices":[{"Role":"target","HasStatus":true,"MassFlow":100.0,"Setpoint":100.0},{"Role":"background","HasStatus":true,"MassFlow":100.0,"Setpoint":100.0}],"SourceConc":100.0,"TotalFlow":200.0,"TargetConc":50.0}
```

字段含义见 [第 5 节](#5-get_status-响应字段)。

---

### 4.2 `set_setpoint` — 设置单路流速设定值

立即写入指定角色（`target` / `background`）对应 MFC 的流速设定值。

**请求：**

```json
{"cmd":"set_setpoint","role":"target","value":100.0}
```

```json
{"cmd":"set_setpoint","role":"background","value":50.0}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 超出范围 | `{"Ok":false,"Error":"setpoint must be between 0 and 500 SCCM", ...}` |
| 角色不存在 | `{"Ok":false,"Error":"unknown role: <role>", ...}` |

> 有效范围：`value` 为 `0.0 ~ 500.0`（SCCM）。`role` 仅接受 `target` / `background`。

---

### 4.3 `set_source_conc` — 设置源气浓度

设置源气浓度（无单位）。仅保存参数值，**不会**触发流速下发；需调用 `set_gas_mixing` 才会下发。

**请求：**

```json
{"cmd":"set_source_conc","value":100}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 超出范围 | `{"Ok":false,"Error":"source concentration must be between 1 and 1000000", ...}` |

> 有效范围：`value` 为 `1 ~ 1000000`（源气浓度必须大于 0）。支持小数。

---

### 4.4 `set_total_flow` — 设置总流速

设置总流速（SCCM）。仅保存参数值，**不会**触发流速下发；需调用 `set_gas_mixing` 才会下发。

**请求：**

```json
{"cmd":"set_total_flow","value":200.0}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 超出范围 | `{"Ok":false,"Error":"total flow must be between 0 and 1000 SCCM", ...}` |

> 有效范围：`value` 为 `0 < value ≤ 1000`（SCCM）。

---

### 4.5 `set_target_conc` — 设置目标浓度

设置目标浓度（无单位）。仅保存参数值，**不会**触发流速下发；需调用 `set_gas_mixing` 才会下发。

**请求：**

```json
{"cmd":"set_target_conc","value":50}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 超出范围 | `{"Ok":false,"Error":"target concentration must be between 0 and 1000000", ...}` |

> 有效范围：`value` 为 `0 ~ 1000000`，且必须 ≤ 源气浓度。支持小数。

---

### 4.6 `set_gas_mixing` — 应用气体混合（下发两路流速）

按稀释公式计算目标气/背景气流量，并写入两路 MFC 的流速设定值。需要三个参数（`source_conc` / `total_flow` / `target_conc`）均已设置。

**请求：**

```json
{"cmd":"set_gas_mixing"}
```

**响应：**

| 场景 | 响应 |
|---|---|
| 成功 | `{"Ok":true,"Error":null, ...}` |
| 源气未设置 | `{"Ok":false,"Error":"source_conc not set", ...}` |
| 总流速未设置 | `{"Ok":false,"Error":"total_flow not set", ...}` |
| 目标浓度未设置 | `{"Ok":false,"Error":"target_conc not set", ...}` |
| 目标浓度超源气 | `{"Ok":false,"Error":"target concentration cannot exceed source concentration", ...}` |
| 计算流速超范围 | `{"Ok":false,"Error":"computed flow is outside 0 - 500 SCCM", ...}` |
| 设备未连接 | `{"Ok":false,"Error":"device not found (not connected)", ...}` |

---

### 4.7 未知命令

**请求：**

```json
{"cmd":"foo"}
```

**响应：**

```json
{"Ok":false,"Error":"unknown command: foo", ...}
```

---

### 4.8 无效请求 / 非法 JSON

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
| `Devices` | array | 设备状态数组，每个元素为 `DeviceStatus`（见下） |
| `SourceConc` | number \| null | 源气浓度（未设置时为 `null`） |
| `TotalFlow` | number \| null | 总流速（SCCM，未设置时为 `null`） |
| `TargetConc` | number \| null | 目标浓度（未设置时为 `null`） |

**`Devices` 数组元素（`DeviceStatus`）字段：**

| 字段 | 类型 | 说明 |
|---|---|---|
| `Role` | string | 设备角色：`target`（目标气）或 `background`（背景气） |
| `HasStatus` | bool | 是否已从设备读到有效状态 |
| `MassFlow` | number | 当前质量流量（SCCM） |
| `Setpoint` | number | 当前流速设定值（SCCM） |

> 说明：数组只包含已连接（已配置串口）的设备；未连接的设备不会出现在数组中。

---

## 6. 气体混合计算公式

当 `source_conc`、`total_flow`、`target_conc` 三项均设置后，程序按稀释公式自动计算两路流速：

```
目标气流量  = 总流速 × 目标浓度 ÷ 源气浓度
背景气流量  = 总流速 − 目标气流量
```

- 计算结果各保留两位小数。
- 两路流速均须落在 `0 ~ 500` SCCM 范围内，否则报错。
- **触发时机**：设置任一参数（`set_source_conc` / `set_total_flow` / `set_target_conc`）只保存参数值、不触发下发；只有调用 `set_gas_mixing`（或 UI 点击 Set）才会计算并下发两路流速。

**示例：**

- `source_conc = 100`、`total_flow = 200`、`target_conc = 50` → 目标气 100 SCCM、背景气 100 SCCM。
- `source_conc = 100`、`total_flow = 200`、`target_conc = 25` → 目标气 50 SCCM、背景气 150 SCCM。

---

## 7. 错误消息一览

| 错误消息 | 触发条件 |
|---|---|
| `invalid request` | 请求缺少 `cmd` 或 `cmd` 为空 |
| `unknown command: <命令>` | 未识别的命令 |
| `bad request: <解析异常消息>` | JSON 无法解析 |
| `setpoint must be between 0 and 500 SCCM` | `set_setpoint` 的 `value` 超出范围 |
| `unknown role: <role>` | `set_setpoint` 的 `role` 不是 `target`/`background`，或对应设备不存在 |
| `source concentration must be between 1 and 1000000` | `set_source_conc` 的 `value` 超出范围 |
| `total flow must be between 0 and 1000 SCCM` | `set_total_flow` 的 `value` 超出范围 |
| `target concentration must be between 0 and 1000000` | `set_target_conc` 的 `value` 超出范围 |
| `target concentration cannot exceed source concentration` | `set_gas_mixing` 时目标浓度大于源气浓度 |
| `computed flow is outside 0 - 500 SCCM` | `set_gas_mixing` 时计算出的目标气或背景气流量超出 0~500 SCCM |
| `source_conc not set` | `set_gas_mixing` 时源气浓度未设置 |
| `total_flow not set` | `set_gas_mixing` 时总流速未设置 |
| `target_conc not set` | `set_gas_mixing` 时目标浓度未设置 |
| `device not found (not connected)` | `set_gas_mixing` 时两路 MFC 未连接 |

---

## 8. 注意事项

1. **命令并发**：多个命令（UI 按钮、pipe 客户端、每秒轮询）共享同一把串口写锁，串行发送，不会出现帧交叉。
2. **混合参数持久化**：`source_conc`、`total_flow` 会写入 `alicat_mfc.ini` 并在程序启动时自动加载；`target_conc` 不持久化（启动后需重新设置）。
3. **触发策略**：`set_source_conc` / `set_total_flow` / `set_target_conc` 只保存参数值、不触发下发；只有 `set_gas_mixing`（或 UI 点击 Set）才会计算并下发两路流速。
4. **响应键名**：为 PascalCase（如 `SourceConc`、`TotalFlow`、`TargetConc`、`Devices`），与请求键名的小写风格不同，外部程序解析时请以 PascalCase 为准。
5. **管道实例冲突**：Windows 允许多个进程对同一管道名创建服务器（会互相抢占连接）。`alicat_mfc` 启动管道前会用客户端探针检测冲突，若名称已被占用则拒绝启动并提示。

# opencode.md

> 本文档为 opencode 专属版本，与 `CLAUDE.md` 内容保持一致，修改时需同步更新。

## 项目概述

**Photoshop Unity Bridge** 是一个 ASP.NET Core Minimal API 服务器，将 Adobe Photoshop COM 自动化封装为 HTTP REST 端点，供 Unity Editor（或其他 HTTP 客户端）远程调用。通过 HTTP 即可查询文档信息、检查图层树、导出 PNG 以及提取矢量路径数据，无需在客户端直接嵌入 COM 逻辑或 ExtendScript。

- **语言：** C#
- **框架：** ASP.NET Core Minimal API（.NET 9）
- **测试框架：** xUnit + coverlet
- **许可证：** MIT
- **作者：** Misaka16608

## 构建与运行

```bash
# 还原依赖
dotnet restore

# 构建
dotnet build

# 运行（默认端口 9876）
dotnet run --project src/PhotoshopUnityBridge

# 指定端口运行
PS_BRIDGE_PORT=9877 dotnet run --project src/PhotoshopUnityBridge
```

**前置条件：**
- .NET 9 SDK
- 已安装并激活的 Adobe Photoshop（需要 `Photoshop.Application` COM 注册）

## 测试

```bash
# 运行全部测试
dotnet test

# 运行特定测试类
dotnet test --filter "FullyQualifiedName~JsHelpersTests"
```

测试位于 `tests/PhotoshopUnityBridge.Tests/`。

- `JsHelpersTests` — 测试 `JsHelpers`（字符串转义、字段过滤），这些逻辑无需 Photoshop 实例即可测试
- `PhotoshopBridge` 和 `PhotoshopService` 依赖 Photoshop COM，无法进行单元测试

## 架构

```
Unity (HTTP 客户端)
        │
        ▼
┌─────────────────────────────┐
│  ASP.NET Core Minimal API   │  Program.cs — 路由注册、CORS
│  http://localhost:9876      │
├─────────────────────────────┤
│  PhotoshopService (Singleton)│  生成 ExtendScript、解析返回结果
├─────────────────────────────┤
│  PhotoshopBridge (Singleton) │  STA 线程 COM 隔离
│  ┌───────────────────────┐  │
│  │  STA 线程             │  │  串行工作队列、超时保护
│  │  BlockingCollection   │  │
│  └──────────┬────────────┘  │
│             ▼               │
│  Photoshop COM（单实例）     │
└─────────────────────────────┘
```

### 关键设计：STA 线程模型

这是整个项目最重要的架构决策。

Photoshop COM 要求 **STA（单线程单元）** 线程模型。ASP.NET Core 的线程池线程默认为 MTA。从 MTA 线程调用 COM 会导致 RPC 封送（marshaling）、重入 bug 和冻结。

解决方案：`PhotoshopBridge` 是一个单例，拥有一个**专用 STA 后台线程**。所有 COM 调用通过 `BlockingCollection<StaWorkItem>` 序列化——HTTP 请求将工作项入队后通过 `TaskCompletionSource` 异步等待。STA 线程从队列中取出并顺序执行。

这意味着：
- 同一时间只有一个 COM 操作在执行（Photoshop 本身就是单实例应用）
- HTTP 请求线程永远不会阻塞在 COM 调用上——它异步等待
- 超时通过 `CancellationTokenSource` 实现：如果 COM 调用超过超时时间，`_comHealthy` 被设为 `false`，所有后续调用快速失败（必须重启服务器恢复）

### 关键文件

| 文件 | 作用 |
|------|------|
| `Program.cs` | ASP.NET Core 主机、端点注册、CORS 设置、日志配置 |
| `PhotoshopBridge.cs` | STA 线程 COM 隔离、`ExecuteJavaScriptAsync()`、工作队列、COM 健康管理 |
| `PhotoshopService.cs` | ExtendScript 生成、JSON 结果解析、HTTP 响应格式化、导出逻辑 |
| `Infrastructure/JsHelpers.cs` | ExtendScript JSON polyfill（ES3 不包含 `JSON.stringify`）、字符串转义、字段过滤 |

## REST API

所有端点位于 `/ps` 路径组下：

| 方法 | 路径 | 用途 |
|--------|------|---------|
| `GET` | `/ps/info` | Photoshop 版本、活动文档、COM 健康状态 |
| `GET` | `/ps/document` | 文档尺寸、分辨率、色彩模式 |
| `GET` | `/ps/layers?fields=...` | 递归图层树，含文字属性（通过 Action Manager 获取） |
| `POST` | `/ps/layers/{index}/export` | 导出单个图层为 PNG |
| `POST` | `/ps/layers/export-batch` | 批量导出多个图层为 PNG |
| `GET` | `/ps/layers/{index}/bezier-data` | 提取形状图层的 VectorMask 贝塞尔路径数据 |

### 日志

日志输出到 **stderr**（`Console.Error`），确保 stdout 不被污染，仅用于 HTTP 响应。

### CORS

允许所有来源、方法和请求头，方便 Unity Editor 从 localhost 访问。

## ExtendScript 执行策略

`PhotoshopBridge.ExecuteJavaScriptInternal()` 依次尝试三种 `DoJavaScript` 参数模式：

1. 默认参数调用
2. 遇到错误 `-2147212704`（对话框相关）时，自动包装脚本以抑制对话框 (`DialogModes.NO`)
3. 依次尝试 mode 1、mode 2（交互模式）
4. 最后手段：try/catch 包装并抑制对话框

不含 `return` 或 `JSON.stringify` 的脚本会自动追加 `'success'` 后缀，确保返回非空字符串。

## ExtendScript 限制

ExtendScript 基于 **ES3**。缺少以下特性：
- `JSON.stringify`（使用 `JsHelpers.JsonPolyfill` 提供的 `_json()` 函数）
- `Array.prototype.map` / `filter`
- 箭头函数
- `let` / `const`
- 模板字符串

所有图层脚本必须使用 ES3 兼容语法编写。

## 图层文字属性提取

文字图层元数据（字体名称、字号、颜色、对齐方式）通过 Photoshop 的 **Action Manager**（`ActionDescriptor`/`ActionReference`）获取，而非 DOM。DOM 的 `textItem.font`/`textItem.size` 仅作为后备方案。Action Manager 代码会遍历 `textStyle` -> `baseParentStyle` 链以获取继承值。

## 注意事项

- 此项目是 Windows 专用项目，依赖 Photoshop COM（仅 Windows 版 Photoshop 提供 COM 接口）
- 服务器启动时会立即尝试连接 Photoshop COM 并进行初始化，如 Photoshop 未安装或未激活则会启动失败
- COM 超时后服务器进入不健康状态，必须重启才能恢复——这是设计决策，因为 COM 对象在超时后的状态不可预测
- 导出的 PNG 图层原点会被平移至 (0,0)，直接适配 Unity 布局坐标系统
- `src/PhotoshopUnityBridge/Assets/` 目录下的 PNG 文件是项目内置的测试/示例图片资源，构建时会被包含在输出目录中

# Photoshop Unity Bridge

Adobe Photoshop COM 自动化的 HTTP REST 桥接服务器，专为 Unity Editor 集成设计。

[English](README.md)

## 概述

这是一个 ASP.NET Core Minimal API 服务器，将 Photoshop 的 COM 接口封装为简洁的 HTTP 端点。Unity（或任何 HTTP 客户端）可以查询文档信息、检查图层、导出 PNG——无需直接嵌入 COM 逻辑或 ExtendScript。

## 快速开始

```bash
dotnet run --project src/PhotoshopUnityBridge
```

服务器默认监听 `http://localhost:9876`。可通过环境变量 `PS_BRIDGE_PORT` 修改端口。

**前置条件：** .NET 9 SDK，已安装并激活的 Adobe Photoshop。

## API 端点

| 方法 | 路径 | 说明 |
|--------|------|-------------|
| `GET` | `/ps/info` | Photoshop 版本、活动文档、COM 健康状态 |
| `GET` | `/ps/document` | 文档尺寸、分辨率、色彩模式 |
| `GET` | `/ps/layers?fields=name,bounds,text,font_size` | 递归图层树，含文字属性 |
| `POST` | `/ps/layers/{index}/export` | 导出单个图层为 PNG |
| `POST` | `/ps/layers/export-batch` | 批量导出多个图层为 PNGs |

### 示例：从 Unity 导出图层

```csharp
using UnityEngine.Networking;
// ...
var req = new ExportRequest { outputPath = @"C:\Output\Bg.png", scale = 0.5, trim = true };
var json = JsonUtility.ToJson(req);
using var uwr = UnityWebRequest.Post("http://localhost:9876/ps/layers/0/export", json);
uwr.SetRequestHeader("Content-Type", "application/json");
await uwr.SendWebRequest();
```

## 架构

```
Unity (HTTP 客户端)
        │
        ▼
┌─────────────────────────────┐
│  ASP.NET Core Minimal API   │  Program.cs — 路由注册、CORS
│  http://localhost:9876      │
├─────────────────────────────┤
│  PhotoshopService           │  生成 ExtendScript，解析返回结果
├─────────────────────────────┤
│  PhotoshopBridge (单例)     │  STA 线程 COM 隔离
│  ┌───────────────────────┐  │
│  │  STA 线程             │  │  串行工作队列、超时保护
│  │  BlockingCollection   │  │
│  └──────────┬────────────┘  │
│             ▼               │
│  Photoshop COM（单实例）     │
└─────────────────────────────┘
```

**核心设计：** 所有 Photoshop COM 调用都在单个专用 STA 线程上执行。HTTP 请求将工作项入队后异步等待——请求线程从不直接接触 COM。这避免了 COM 封送错误，并实现了超时支持。

## 图层导出输出

每个导出的图层生成一个 PNG，具有以下特性：
- 图层原点平移至 (0,0)——可直接在 Unity 中布局
- 可选缩放（如 `0.5` 表示 50%）
- 可选透明像素裁剪
- 透明背景 (`DocumentFill.TRANSPARENT`)

## 许可证

MIT

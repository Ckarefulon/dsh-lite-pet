# GPU 硬解迁移（进行中）

目标：把 640×720 的 H.264 解码从 CPU 软解（实测 5.7ms/帧）迁到 D3D11 硬解，CPU 目标 < 5% 单核。

## 已确认可用
- SharpDX 4.2（net45，无 netstandard 依赖链）：
  `SharpDX.dll` / `SharpDX.DXGI.dll` / `SharpDX.Direct3D11.dll` / `SharpDX.Mathematics.dll` / `SharpDX.MediaFoundation.dll`，合计约 1.5MB。
- D3D11 设备创建（VideoSupport|BgraSupport）、AMD 适配器枚举、`MFCreateDXGIDeviceManager` 均成功。
- 视频处理器链路签名已核实（`VideoDevice.CreateVideoProcessor*`、`VideoProcessorBlt`、`VideoProcessorSetStreamSourceRect`、`MapSubresource`）。
- 关键 GUID：`MF_SOURCE_READER_D3D_MANAGER = ec822da2-e1e9-4b29-a0d8-563c-719f5269`，
  `IMFDXGIBuffer = e7174cfa-1c9e-48b1-8866-626226bfc258`，
  `IMFDXGIDeviceManager = eb533d5d-2db6-40f8-97a9-494692014f07`，
  `ID3D11Texture2D = 6f15aaf2-d208-4e89-9ab4-489535d34f9c`。

## 当前卡点（实测数据）
`IMFDXGIDeviceManager::ResetDevice` 不接受 token：
- token = 0 → `E_INVALIDARG (0x80070057)`
- `MFCreateDXGIDeviceManager` 返回的 token（如 119247875）→ `E_HANDLE (0x80070006)`
已排除：设备标志（原生 0x820 / SharpDX / 逐适配器）、`MFSTARTUP_FULL`、D3D11 vs DXGI 指针、全新 manager 实例。
SharpDX 生成的 `MediaFactory.CreateDXGIDeviceManager` 第二参数被错误声明为值传递（拿不到 manager/token），别用。

## 下一步实验（按顺序）
1. DXVA2 路线：`MFCreateDeviceManager` → `IDirect3DDeviceManager9::ResetDevice(D3D9Ex, token)`，用 D3D9Ex 设备绕开 DXGI manager 的 token 校验。
2. H.264 硬解 MFT：`MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG_HARDWARE)` + `MFT_MESSAGE_SET_D3D_MANAGER`，自行管理 D3D 纹理，不经 SourceReader。
3. 独立 MTA 线程 + 显式 `CoInitializeEx(COINIT_MULTITHREADED)` 重建 manager/device（排除消息循环干扰）。

## 迁移完成后的落点
- `AnimationClip`：从 `Mf.Read` 改为「取纹理 → 视频处理器（NV12→BGRA，同时缩放到窗口尺寸）→ staging 回读」。
- 彩色/遮罩两次 Blt 到两张 BGRA 纹理；回读用双 staging + `Map(DoNotWait)`，未就绪复用上一帧。
- 回读后 premultiply；命中判定改用 1/4 分辨率 mask 查表；全程物理像素 1:1。

# vendor/webp

把转码好的**动画 WebP**（带 alpha）放在这里，文件名与上游动画名一致，例如：

```
vendor/webp/待机呼吸休闲.webp
vendor/webp/工作状态-忙碌点按.webp
vendor/webp/悠闲哼歌.webp
```

然后运行：

```bash
node tools/build-media.mjs     # 分类 + 生成 runtime/media/index.json
node tools/build-pet.mjs       # 编译桌宠并把 libwebp 拷到 runtime/pet
```

要点：

- 画布必须是 **640×360**（与 `index.json` 的 `width/bodyHeight` 一致），否则该段会被跳过。
- 必须是**动画** WebP（含 `ANIM`/`ANMF` 块）；静态 WebP 会被判为无效并跳过。
- 建议参数：`ffmpeg -i clip.webm -vf "fps=<源帧率>,scale=640:360" -c:v libwebp_anim -lossless 0 -q:v 80 -loop 0 clip.webp`。
- 体积随帧率/质量线性增长；`-q:v` 65–80、按需降帧率都能显著缩小。

`index.json` 里 `kind` 的取值：`idle` / `work-thinking` / `work-busy` / `work-waiting` /
`work-done` / `work-error` / `action` / `click` / `whisper` / `balance` / `sleep`。

/**
 * Mock 识别实现：不看音频内容，固定回一句带字节数的文字。
 *
 * 存在意义是验证 `petApi7340.speech.register` 这条第三方注册入口——不联网、不加载模型，
 * 开关打开后 `POST /voice/hear` 改走这里；回的字节数与发出去的对得上，
 * 就说明音频真的穿过 base64 与 HTTP 到达了宿主。
 * 默认不注册：开关由本仓库的 `/cordis-probe-speech` 路由控制（见 ./toggle-route.js）。
 * @module dsh-cordis-probe/mock-speech
 */

/**
 * 造一个 provider：不论文本与语速，一律回同一段假转写。
 * @returns 可直接交给 `speech.register` 的定义。
 */
export function createMockSpeechProvider() {
  return {
    id: 'mock-asr',
    name: 'Mock 识别',
    async transcribe({ audio, language }) {
      const suffix = language === undefined ? '' : '，语言 ' + language;
      return 'mock 识别：收到 ' + audio.byteLength + ' 字节' + suffix;
    },
  };
}

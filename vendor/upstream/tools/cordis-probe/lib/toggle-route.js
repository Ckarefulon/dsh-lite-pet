/**
 * 开关路由：`GET` 读当前状态、`POST` 翻转。
 *
 * 语音合成与语音识别各有一个开关，两者的路由行为完全一样、只有路径不同，
 * 所以这里按路径造 handler，不各写一份。
 * 存在的理由：注册在宿主（Node），按钮在页面——两半没有直接通道，只能由宿主开一个最小路由。
 * 只服务本示例，不进 dsh-pet 的接口面。
 * @module dsh-cordis-probe/toggle-route
 */

/** 语音合成开关的路径（与 dsh-pet 的 `/dsh-pet-7340` 并列，互不干扰）。 */
export const VOICE_ROUTE = '/cordis-probe-voice';
/** 语音识别开关的路径。 */
export const SPEECH_ROUTE = '/cordis-probe-speech';

/**
 * 造一个开关处理器。
 *
 * @param control - 读写开关的两个函数；`setOn` 返回生效后的状态。
 * @returns 可直接交给 `webServer.register` 的 handler。
 */
export function createToggleHandler(control) {
  return (req, res) => {
    if (req.method === 'GET') {
      respond(res, 200, { ok: true, on: control.isOn() });
      return;
    }
    if (req.method !== 'POST') {
      respond(res, 405, { ok: false, reason: 'method-not-allowed' });
      return;
    }
    // 不带请求体：点一次就是翻转一次，省掉解析与校验
    respond(res, 200, { ok: true, on: control.setOn(!control.isOn()) });
  };
}

/** 写一个 JSON 响应。 */
function respond(res, status, body) {
  const text = JSON.stringify(body);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(text),
  });
  res.end(text);
}

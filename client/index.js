// dsh-lite-pet — browser half. Adds exactly one switch row to 设置 → 通用; the
// row drives the Host's `/api/dsh-lite-pet` endpoints, nothing else is registered.
window.__ModuleLoader__.load({
  id: 'dsh-lite-pet',
  factory: (require) => {
    const module = { exports: {} }
    const exports = module.exports
    Object.defineProperty(exports, Symbol.toStringTag, { value: 'Module' })

    const React = require('react')
    const h = React.createElement
    const useState = React.useState
    const useEffect = React.useEffect
    const useCallback = React.useCallback

    const STATE_PATH = '/api/dsh-lite-pet/state'
    const ENABLED_PATH = '/api/dsh-lite-pet/enabled'

    const TEXT = {
      title: '桌面桌宠',
      description:
        '在桌面上显示 DSH 小女仆：工作时冒出最新一段正文，等你确认时亮黄点，完成后显示「会话名」已完成；点击打开 DSH，右键换动作 / 查余额 / 关闭。',
      on: '已开启',
      off: '已关闭',
      unsupported: '仅支持 Windows',
      failed: '读取状态失败',
      toggleFailed: '切换失败',
    }

    async function readJson(path, init) {
      const response = await fetch(path, init)
      const body = await response.json().catch(() => null)
      return body
    }

    function Row(props) {
      const ctx = props.ctx
      const [state, setState] = useState(null)
      const [busy, setBusy] = useState(false)
      const [error, setError] = useState(null)

      const load = useCallback(async () => {
        try {
          const body = await readJson(STATE_PATH)
          if (body !== null && body.ok === true) {
            setState(body)
            setError(null)
          } else {
            setError(TEXT.failed)
          }
        } catch {
          setError(TEXT.failed)
        }
      }, [])

      useEffect(() => {
        void load()
      }, [load])

      const toggle = useCallback(async () => {
        if (state === null || busy) return
        const next = state.enabled !== true
        setBusy(true)
        try {
          const body = await readJson(ENABLED_PATH, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ enabled: next }),
          })
          if (body !== null && body.ok === true) {
            setState(body)
            setError(null)
          } else {
            setError(body !== null && typeof body.error === 'string' ? body.error : TEXT.toggleFailed)
            void load()
          }
        } catch {
          setError(TEXT.toggleFailed)
          void load()
        } finally {
          setBusy(false)
        }
      }, [state, busy, load])

      const supported = state === null ? true : state.supported !== false
      const enabled = state !== null && state.enabled === true
      const status = state === null ? '' : supported ? (enabled ? TEXT.on : TEXT.off) : TEXT.unsupported

      void ctx

      return h(
        'div',
        {
          style: {
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: '24px',
            padding: '10px 0',
          },
        },
        h(
          'div',
          { style: { display: 'flex', flexDirection: 'column', gap: '3px', minWidth: 0 } },
          h(
            'div',
            { style: { fontSize: '13px', fontWeight: 600, color: 'var(--dsw-alias-label-primary, #1a1a1a)' } },
            TEXT.title,
            h(
              'span',
              { style: { marginLeft: '8px', fontSize: '12px', fontWeight: 400, color: 'var(--dsw-alias-label-secondary, #8a8a8a)' } },
              status,
            ),
          ),
          h(
            'div',
            { style: { fontSize: '12.5px', lineHeight: 1.5, color: 'var(--dsw-alias-label-secondary, #8a8a8a)' } },
            error !== null ? error : TEXT.description,
          ),
        ),
        h(Switch, {
          checked: enabled,
          disabled: !supported || busy || state === null,
          onChange: toggle,
        }),
      )
    }

    function Switch(props) {
      const enabled = props.checked === true
      const disabled = props.disabled === true
      return h(
        'button',
        {
          type: 'button',
          role: 'switch',
          'aria-checked': enabled,
          disabled,
          onClick: props.onChange,
          style: {
            flex: 'none',
            width: '38px',
            height: '22px',
            padding: '2px',
            border: 'none',
            borderRadius: '11px',
            cursor: disabled ? 'default' : 'pointer',
            opacity: disabled ? 0.5 : 1,
            background: enabled
              ? 'var(--dsw-alias-brand-primary, #4d6bfe)'
              : 'var(--dsw-alias-border-l2, #d0d0d0)',
            transition: 'background 120ms ease',
          },
        },
        h('span', {
          style: {
            display: 'block',
            width: '18px',
            height: '18px',
            borderRadius: '50%',
            background: '#ffffff',
            boxShadow: '0 1px 2px rgba(0,0,0,.25)',
            transform: enabled ? 'translateX(16px)' : 'translateX(0)',
            transition: 'transform 120ms ease',
          },
        }),
      )
    }

    function apply(ctx) {
      ctx.slots.inject('settings.general.item', () =>
        ctx.slots.register(
          {
            name: 'settings.general.item',
            id: 'dsh-lite-pet',
            // After performance-usage (30), before current-version (100); the tailscale row sits at 95.
            order: 96,
            inject: () => ({ ctx }),
          },
          Row,
        ),
      )
    }

    exports.name = 'dsh-lite-pet'
    exports.inject = ['slots']
    exports.apply = apply
    return module.exports
  },
})

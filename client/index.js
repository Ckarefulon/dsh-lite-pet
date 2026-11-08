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

    function Row() {
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

      // The pet can also be closed from its own right-click menu, so re-read the
      // state whenever the window regains focus.
      useEffect(() => {
        const onFocus = () => {
          void load()
        }
        window.addEventListener('focus', onFocus)
        return () => window.removeEventListener('focus', onFocus)
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

      return h(
        'div',
        {
          // Addressed by the sidebar shortcut so it can scroll this row into view.
          'data-dsh-lite-pet-row': '',
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

    // ——— sidebar workspace-header button ————————————————————————————————
    //
    // The workspace section header has no slot of its own, so this is the same DOM
    // augmentation the official `dsh-codespace-workspace` plugin uses: one coalesced
    // MutationObserver, inject-only, never removing or replacing a React-owned node,
    // everything tagged `data-dsh-lite-pet-*`, and a full undo on dispose.
    //
    // Host class names come from a hashed CSS module (`<hash>_headerActions`), and the
    // hash changes between builds, so every lookup matches the SUFFIX through an
    // attribute-contains selector. If a future build renames a suffix the button is
    // simply skipped; nothing else breaks.
    //
    // The row is `<sectionHeader>` = [label][searchSlot][headerActions]. The user asked
    // for the seat "before 搜索", i.e. immediately left of the search box, which is what
    // inserting ahead of `headerActions` gives us.
    const SEARCH_SLOT_SUFFIX = '_searchSlot'
    const HEADER_ACTIONS_SUFFIX = '_headerActions'
    const SECTION_HEADER_SUFFIX = '_sectionHeader'
    const GLYPH_STROKE = 1 // Regular weight, matching the host's own outline icons

    /**
     * Keeps one button inserted in the workspace section header, immediately left of
     * the search box. Returns an undo function.
     */
    function augmentWorkspaceHeader(onClick) {
      let root = null
      let button = null
      let frame = 0

      const teardown = () => {
        if (button !== null && button.parentNode !== null) button.parentNode.removeChild(button)
        button = null
      }

      const place = () => {
        const header = document.querySelector(`[class*="${SECTION_HEADER_SUFFIX}"]`)
        if (header === null) {
          teardown() // the section is not on screen (another settings page is open)
          return
        }
        root = header
        const actions = header.querySelector(`[class*="${HEADER_ACTIONS_SUFFIX}"]`)
        // Insert before the actions group: that is the "left of 搜索" seat the user
        // asked for, and it survives the headerActions collapse while searching.
        if (actions === null) {
          teardown()
          return
        }
        if (button === null) {
          button = document.createElement('button')
          button.type = 'button'
          button.setAttribute('data-dsh-lite-pet-header', '')
          button.setAttribute('aria-label', TEXT.title)
          button.title = TEXT.title
          button.style.cssText = [
            'flex:none',
            'display:inline-flex',
            'align-items:center',
            'justify-content:center',
            'width:28px',
            'height:28px',
            'padding:0',
            'border:none',
            'border-radius:var(--dsw-radius-md, 8px)',
            'background:transparent',
            'color:var(--dsw-alias-label-secondary, #8a8a8a)',
            'cursor:pointer',
          ].join(';')
          button.addEventListener('click', onClick)
          button.addEventListener('mouseenter', () => {
            button.style.background = 'var(--dsw-alias-interactive-hover, rgba(0,0,0,.06))'
          })
          button.addEventListener('mouseleave', () => {
            button.style.background = 'transparent'
          })
          // The glyph is drawn with the same 16px outline spec as the host's icons.
          button.innerHTML =
            '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true" ' +
            `stroke="currentColor" stroke-width="${GLYPH_STROKE}" stroke-linecap="round" ` +
            'stroke-linejoin="round" style="display:block">' +
            '<path d="M1.6 8.6c0-3.4 2.8-5.9 6.2-5.9 3.6 0 6.6 2.6 6.6 5.6 0 2.4-2 4.1-4.6 4.1-1.6 0-3-.6-4.1-1.6-.5-.5-1.2-.6-1.8-.3-.7.3-1.3.1-1.6-.5-.3-.5-.4-1-.4-1.4z"/>' +
            '<circle cx="10.2" cy="6.9" r="0.85"/>' +
            '<path d="M1.9 8.9C1.2 8.6.7 8.2.4 7.9c.5-.2 1.1-.3 1.8-.2"/>' +
            '<path d="M5.6 13.1c-.5.7-.9 1.2-1.4 1.5-.2-.6-.2-1.2 0-1.8"/>' +
            '</svg>'
        }
        if (button.parentNode !== header) {
          header.insertBefore(button, actions)
          return
        }
        // Already in this header; only move it if something displaced it.
        if (button.nextSibling !== actions) header.insertBefore(button, actions)
      }

      const schedule = () => {
        if (frame !== 0) return
        frame = requestAnimationFrame(() => {
          frame = 0
          try {
            place()
          } catch {
            /* a DOM shape we do not recognise must never break the row */
          }
        })
      }

      // One observer, coalesced through requestAnimationFrame: React re-renders the
      // header on every search keystroke and on every section switch.
      const observer = new MutationObserver(schedule)
      observer.observe(document.body, { childList: true, subtree: true })
      place()

      return () => {
        observer.disconnect()
        if (frame !== 0) cancelAnimationFrame(frame)
        teardown()
        if (root !== null) root = null
      }
    }

    function apply(ctx) {
      ctx.slots.inject('settings.general.item', () =>
        ctx.slots.register(
          {
            name: 'settings.general.item',
            id: 'dsh-lite-pet',
            // After performance-usage (30), before current-version (100); the tailscale row sits at 95.
            order: 96,
          },
          Row,
        ),
      )

      // Clicking the header button opens the pet's own settings row by scrolling it into
      // view; it is a shortcut, not a second switch.
      ctx.effect(() =>
        augmentWorkspaceHeader(() => {
          const items = document.querySelectorAll('[data-dsh-lite-pet-row]')
          const first = items[0]
          if (first !== undefined && typeof first.scrollIntoView === 'function') {
            first.scrollIntoView({ block: 'center', behavior: 'smooth' })
          }
        }),
      )
    }

    exports.name = 'dsh-lite-pet'
    exports.inject = ['slots']
    exports.apply = apply
    return module.exports
  },
})



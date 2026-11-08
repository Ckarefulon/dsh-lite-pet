// dsh-lite-pet — browser half.
//
// There is deliberately NO settings row. The pet's only in-app control is one icon in
// the sidebar workspace header, and that icon IS the switch: click to turn the pet on,
// click again to turn it off. Everything else lives on the pet itself (right-click menu).
//
// The workspace header has no slot, so the icon is injected with the same DOM
// augmentation discipline the official plugins use: one coalesced MutationObserver,
// inject-only, nothing React owns is removed or replaced, everything tagged
// `data-dsh-lite-pet-*`, and a full undo on dispose.
window.__ModuleLoader__.load({
  id: 'dsh-lite-pet',
  factory: (require) => {
    const module = { exports: {} }
    const exports = module.exports
    Object.defineProperty(exports, Symbol.toStringTag, { value: 'Module' })

    const STATE_PATH = '/api/dsh-lite-pet/state'
    const ENABLED_PATH = '/api/dsh-lite-pet/enabled'

    const TEXT = {
      title: '桌面桌宠',
      on: '桌面桌宠：已开启（点击关闭）',
      off: '桌面桌宠：已关闭（点击开启）',
      unsupported: '桌面桌宠：仅支持 Windows',
      failed: '桌面桌宠：读取状态失败',
      toggleFailed: '桌面桌宠：切换失败',
    }

    // Host class names come from a hashed CSS module (`<hash>_headerActions`), and the
    // hash changes between builds, so every lookup matches the SUFFIX through an
    // attribute-contains selector. A renamed suffix simply skips the button.
    const SEARCH_SLOT_SUFFIX = '_searchSlot'
    const HEADER_ACTIONS_SUFFIX = '_headerActions'
    const SECTION_HEADER_SUFFIX = '_sectionHeader'

    // The host's own outline icons (`Icon*OutlineRegular` from
    // `@deepseek-ai/dsh-client-ui-primitives`) share one artwork convention:
    // `<svg width height viewBox="0 0 16 16" fill="none" stroke="currentColor"
    // stroke-width={1}>`, with round caps and joins. Drawing on the same 16-unit grid at
    // the same stroke width is what makes this sit in the row as a peer rather than
    // looking bolder or lighter than the buttons beside it.
    const GLYPH =
      '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true" ' +
      'stroke="currentColor" stroke-width="1" ' +
      'stroke-linecap="round" stroke-linejoin="round" style="display:block">' +
      // Water-drop body.
      '<path d="M8 1.92C4.8 6.08 3.2 8.32 3.2 10.56A4.8 4.8 0 0 0 12.8 10.56C12.8 8.32 11.2 6.08 8 1.92Z"/>' +
      // Two eyes, mirrored about the centre.
      '<path d="M5.6 8.96v1.92"/>' +
      '<path d="M10.4 8.96v1.92"/>' +
      '</svg>'

    async function readJson(path, init) {
      const response = await fetch(path, init)
      return await response.json().catch(() => null)
    }

    /**
     * Reads and toggles the pet through the Host's `/api/dsh-lite-pet` routes, which sit
     * behind the normal `/api` fence and carry the browser cookie. Returns a controller
     * whose `paint` is handed to the DOM augmentation.
     */
    function createSwitch() {
      const view = { enabled: false, supported: true, busy: false, error: null }
      let button = null
      let disposed = false

      const title = () => {
        if (view.busy) return TEXT.title
        if (!view.supported) return TEXT.unsupported
        if (view.error !== null) return view.error
        return view.enabled ? TEXT.on : TEXT.off
      }

      const paint = () => {
        if (button === null || disposed) return
        button.title = title()
        button.setAttribute('aria-label', title())
        button.setAttribute('aria-pressed', view.enabled ? 'true' : 'false')
        button.disabled = view.busy || !view.supported
          // No colour or opacity difference between on and off: the icon is a peer
          // of the buttons beside it and has to keep their exact appearance. State
          // is carried by aria-pressed and the tooltip, not by recolouring the glyph.
          // Only the disabled state differs, which is platform convention.
          button.style.cursor = view.busy || !view.supported ? 'default' : 'pointer'
          button.style.opacity = view.busy || !view.supported ? '0.5' : '1'
      }

      const refresh = async () => {
        try {
          const body = await readJson(STATE_PATH)
          if (body === null || body.ok !== true) {
            view.error = TEXT.failed
          } else {
            view.enabled = body.enabled === true
            view.supported = body.supported !== false
            view.error = null
          }
        } catch {
          view.error = TEXT.failed
        }
        paint()
      }

      const toggle = async () => {
        if (view.busy || !view.supported) return
        const next = !view.enabled
        view.busy = true
        view.enabled = next // optimistic; reverted below if the write fails
        paint()
        try {
          const body = await readJson(ENABLED_PATH, {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ enabled: next }),
          })
          if (body !== null && body.ok === true) {
            view.enabled = body.enabled === true
            view.error = null
          } else {
            view.enabled = !next
            view.error = body !== null && typeof body.error === 'string' ? body.error : TEXT.toggleFailed
          }
        } catch {
          view.enabled = !next
          view.error = TEXT.toggleFailed
        } finally {
          view.busy = false
          paint()
        }
      }

      return {
        paint,
        refresh,
        toggle,
        attach: (element) => {
          button = element
          paint()
        },
        detach: () => {
          button = null
        },
        dispose: () => {
          disposed = true
          button = null
        },
      }
    }

    /**
     * Keeps the pet switch inserted in the workspace section header, immediately left of
     * the search box. Returns an undo function.
     */
    function augmentWorkspaceHeader(control) {
      let button = null
      let frame = 0

      const teardown = () => {
        if (button !== null && button.parentNode !== null) button.parentNode.removeChild(button)
        control.detach()
        button = null
      }

      const build = () => {
        const element = document.createElement('button')
        element.type = 'button'
        element.setAttribute('data-dsh-lite-pet-header', '')
        element.style.cssText = [
          // Copied from the host's own header iconButton (WorkspaceBrowser): 28x28,
          // radius-sm, label-secondary ink, no background. Hover swaps only the
          // background. Deliberately NOT the 16x16 row-button variant that shares the
          // class suffix — that one belongs to a different module and looked undersized.
          'width:28px',
          'height:28px',
          'padding:0',
          'border:none',
          'border-radius:var(--dsw-radius-sm, 6px)',
          'background:none',
          'color:var(--dsw-alias-label-secondary, #8a8a8a)',
          'cursor:pointer',
        ].join(';')
        element.addEventListener('click', () => {
          void control.toggle()
        })
        // The host's rule is exactly `_iconButton:hover{background:var(--dsw-alias-interactive-bg-hover)}`
        // with no ink change and no border. Copying it verbatim is what makes this
        // button indistinguishable from the icons beside it.
        element.addEventListener('mouseenter', () => {
          element.style.background = 'var(--dsw-alias-interactive-bg-hover, rgba(0,0,0,.06))'
        })
        element.addEventListener('mouseleave', () => {
          element.style.background = 'none'
        })
        element.innerHTML = GLYPH
        control.attach(element)
        return element
      }

      const place = () => {
        const header = document.querySelector(`[class*="${SECTION_HEADER_SUFFIX}"]`)
        if (header === null) {
          // Another settings page is open, or the sidebar is in a shape we do not know.
          teardown()
          return
        }
        // The header is [label][searchSlot][headerActions]. The seat asked for is the one
        // LEFT of the search box, so the anchor is the search slot itself — anchoring on
        // headerActions instead puts the icon to the RIGHT of the search box, because the
        // actions group comes after it in document order.
        const search = header.querySelector(`[class*="${SEARCH_SLOT_SUFFIX}"]`)
        // Sit between 搜索 and the icon group on its right. sectionHeader is
        // `justify-content:flex-end` with `gap:4px`, so a node in that position reads
        // as part of the same icon run while still being left of the search box — the
        // two requirements the user asked for are not actually in conflict.
        const actions = header.querySelector(`[class*="${HEADER_ACTIONS_SUFFIX}"]`)
        const anchor = actions !== null
          ? actions
          : search !== null
            ? search.nextSibling
            : null
        if (anchor === null) {
          teardown()
          return
        }
        if (button === null) button = build()
        if (button.parentNode !== header || button.nextSibling !== anchor) {
          header.insertBefore(button, anchor)
        }
        control.paint()
      }

      const schedule = () => {
        if (frame !== 0) return
        frame = requestAnimationFrame(() => {
          frame = 0
          try {
            place()
          } catch {
            /* an unfamiliar DOM shape must never break the row */
          }
        })
      }

      // One observer, coalesced through requestAnimationFrame: React re-renders the
      // header on every search keystroke and on every settings-section switch.
      const observer = new MutationObserver(schedule)
      observer.observe(document.body, { childList: true, subtree: true })
      place()

      return () => {
        observer.disconnect()
        if (frame !== 0) cancelAnimationFrame(frame)
        teardown()
      }
    }

    function apply(ctx) {
      const control = createSwitch()

      ctx.effect(() => {
        void control.refresh()
        // The pet can also be switched off from its own right-click menu (关闭桌宠), so
        // re-read whenever the window regains focus instead of trusting a stale value.
        const onFocus = () => {
          void control.refresh()
        }
        window.addEventListener('focus', onFocus)
        const undo = augmentWorkspaceHeader(control)
        return () => {
          window.removeEventListener('focus', onFocus)
          undo()
          control.dispose()
        }
      })
    }

    exports.name = 'dsh-lite-pet'
    // Every client half in this profile declares an inject list; an empty one is the safe
    // form when no service is read. `slots` is not needed any more (no slot is registered),
    // but omitting the field entirely is untested territory in this shell.
    exports.inject = []
    exports.apply = apply
    return module.exports
  },
})

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  InjectionToken,
  ViewEncapsulation,
  afterNextRender,
  inject,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import { PaneSize } from './terminal-session';

/**
 * What the pane needs from a terminal emulator. `@xterm/xterm` plus its fit addon has this shape;
 * the seam exists so a spec can mount a recording screen in jsdom, where xterm's renderer has no
 * layout to measure, and so the emulator — 300 KB before gzip — is loaded with the terminal's own
 * chunk and only in a browser.
 */
export interface TerminalScreen {
  /** Mounts into the host element and takes its size from it. */
  open(host: HTMLElement): void;
  /** Bytes or text the shell printed. */
  write(data: Uint8Array | string): void;
  /** A line from the portal, printed so it cannot be mistaken for the shell's own output. */
  notice(text: string): void;
  /** What the person typed, as xterm reports it — a string, including escape sequences for keys. */
  onData(handler: (data: string) => void): void;
  /** Fits the grid to the host and reports the size it settled on. */
  fit(): PaneSize;
  focus(): void;
  dispose(): void;
}

/**
 * Loads a screen. The default is xterm, imported on first use so that it is neither in the
 * initial bundle (docs/plan/20 § Performance budget) nor on the server, where it has no `document`.
 */
export const TERMINAL_SCREEN_FACTORY = new InjectionToken<() => Promise<TerminalScreen>>('cc.terminal.screen', {
  providedIn: 'root',
  factory: () => async () => {
    const [{ Terminal }, { FitAddon }] = await Promise.all([import('@xterm/xterm'), import('@xterm/addon-fit')]);
    const terminal = new Terminal({ cursorBlink: true, fontSize: 13, scrollback: 5000, convertEol: false });
    const fit = new FitAddon();
    terminal.loadAddon(fit);

    return {
      open: host => terminal.open(host),
      write: data => terminal.write(data),
      notice: text => terminal.write(`\r\n\u001b[2m— ${text} —\u001b[22m\r\n`),
      onData: handler => terminal.onData(handler),
      fit: () => {
        fit.fit();
        return { cols: terminal.cols, rows: terminal.rows };
      },
      focus: () => terminal.focus(),
      dispose: () => terminal.dispose()
    };
  }
});

/**
 * The terminal emulator in a box: docs/plan/20 § The pages that are not generated, "`xterm.js` in
 * a dockable panel".
 *
 * The pane owns the emulator and nothing else. Keystrokes go out through `data`, the size goes out
 * through `resized` whenever the box changes, and `write`/`notice` paint what comes back; the
 * session that connects the two is `TerminalSession`, held by the blade. Keeping the socket out of
 * here is what lets the blade reconnect the same pane to a new session without redrawing it.
 *
 * ⚠ **`ViewEncapsulation.None`, for xterm's stylesheet.** The emulator builds its own DOM inside
 * the host and its CSS addresses those elements by class; a scoped stylesheet would carry an
 * attribute xterm's elements never get, so the rules would match nothing. The stylesheet is
 * imported from the package rather than copied into the tree — one version, pinned by
 * `package.json`, and nothing for prettier to reformat. It travels with this component's chunk,
 * so a page without a terminal pays nothing for it.
 */
@Component({
  selector: 'cc-terminal-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  styles: `
    @import '@xterm/xterm/css/xterm.css';
  `,
  host: { class: 'block' },
  template: `
    <div
      #host
      class="cc-terminal-host h-full min-h-72 w-full overflow-hidden rounded-md bg-black p-2"
      role="application"
      [attr.aria-label]="label()"
      [attr.aria-busy]="ready() ? null : 'true'"
      data-terminal-host
    ></div>
  `
})
export class TerminalPane {
  /** The accessible name of the box — which console this is. */
  readonly label = input.required<string>();

  /** What the person typed. */
  readonly data = output<string>();
  /** The grid's size, on mount and after every fit. */
  readonly resized = output<PaneSize>();

  private readonly hostElement = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private readonly loadScreen = inject(TERMINAL_SCREEN_FACTORY);
  private readonly destroyRef = inject(DestroyRef);

  private screen: TerminalScreen | null = null;
  /** Bytes that arrived before the emulator was mounted. Replayed in order once it is. */
  private pending: (Uint8Array | string)[] = [];
  protected readonly ready = signal(false);

  constructor() {
    // Browser only: xterm needs a document to draw into, and the server render of this page is
    // the box with nothing in it. `afterNextRender` never runs during SSR.
    afterNextRender(() => void this.mount());

    this.destroyRef.onDestroy(() => {
      this.screen?.dispose();
      this.screen = null;
    });
  }

  /** Paints shell output. */
  write(data: Uint8Array | string): void {
    if (this.screen === null) this.pending.push(data);
    else this.screen.write(data);
  }

  /** Paints a line from the portal, visibly not the shell's. */
  notice(text: string): void {
    if (this.screen === null) this.pending.push(`\r\n— ${text} —\r\n`);
    else this.screen.notice(text);
  }

  /** Fits the grid to the box and reports the size. The current size, or 80×24 before mount. */
  fit(): PaneSize {
    if (this.screen === null) return { cols: 80, rows: 24 };
    const size = this.screen.fit();
    this.resized.emit(size);
    return size;
  }

  focus(): void {
    this.screen?.focus();
  }

  private async mount(): Promise<void> {
    const screen = await this.loadScreen();
    // Destroyed while the chunk was loading — the blade closed.
    if (this.destroyRef.destroyed) {
      screen.dispose();
      return;
    }

    this.screen = screen;
    screen.open(this.hostElement().nativeElement);
    screen.onData(data => this.data.emit(data));

    for (const chunk of this.pending) screen.write(chunk);
    this.pending = [];
    this.ready.set(true);

    this.fit();

    // A dockable panel changes size without the window doing so; the box is what to watch. jsdom
    // has no ResizeObserver, and a pane there keeps the size it mounted with.
    if (typeof ResizeObserver !== 'undefined') {
      const observer = new ResizeObserver(() => this.fit());
      observer.observe(this.hostElement().nativeElement);
      this.destroyRef.onDestroy(() => observer.disconnect());
    }
  }
}

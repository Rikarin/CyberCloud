import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ShellLayout } from '@cybercloud/shell';

/**
 * The application root. It is the shell and nothing else — every page is reached through the
 * router outlet inside `ShellLayout`, which is what keeps the initial bundle to the shell.
 */
@Component({
  selector: 'cc-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ShellLayout],
  template: '<cc-shell />',
})
export class App {}

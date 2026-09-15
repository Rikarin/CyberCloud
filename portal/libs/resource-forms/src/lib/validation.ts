import { FormGroup } from '@angular/forms';
import { controlsByPointer } from './form-model';

/**
 * One validation message, from either the schema or the server. One shape, per docs/plan/20's
 * `validation/` box: "schema + async server validation, one message shape".
 */
export interface FormValidationMessage {
  /** JSON Pointer into the form value. Empty string for a form-level message. */
  readonly pointer: string;
  readonly message: string;
  readonly severity: 'error' | 'warning';
}

/** The platform's error, as the transport hands it over — docs/plan/08 § Errors. */
export interface PlatformError {
  readonly code: string;
  readonly message: string;
  /** ⚠ A JSON Pointer into the request body, so the portal can highlight the field. */
  readonly target?: string;
  readonly details?: readonly PlatformError[];
}

/**
 * Flattens a platform error into messages, one per leaf.
 *
 * A `SchemaInvalid` answer carries one detail per refused member, each with its own `target`;
 * the outer message is a summary. The leaves are what a form can act on, so they are returned and
 * the summary is kept only when there are no leaves to return — otherwise the same failure would
 * be shown twice, once on the field and once above the form.
 */
export function messagesOf(error: PlatformError): readonly FormValidationMessage[] {
  const leaves = (error.details ?? []).flatMap(messagesOf);

  if (leaves.length > 0) return leaves;

  return [{ pointer: error.target ?? '', message: error.message, severity: 'error' }];
}

/**
 * Puts each message on the control its pointer names, and returns the ones nothing claimed.
 *
 * A message on a control shows under that field, through the same `messageFor` path a schema
 * validator's error takes — that is the "one message shape". A pointer no control has (a member
 * the form does not render, or the empty pointer) is returned for the form to show as a banner,
 * so a server complaint is never lost because the portal had nowhere to put it.
 *
 * ⚠ The server error is cleared on the next change to that control, by re-running the schema
 * validators without it. Leaving it would make a corrected field look wrong until the next submit.
 */
export function applyServerErrors(
  group: FormGroup,
  messages: readonly FormValidationMessage[]
): readonly FormValidationMessage[] {
  const controls = controlsByPointer(group);
  const unclaimed: FormValidationMessage[] = [];

  for (const message of messages) {
    const control = controls.get(message.pointer);

    if (control === undefined) {
      unclaimed.push(message);
      continue;
    }

    control.setErrors({ ...(control.errors ?? {}), server: message.message });
    control.markAsTouched();

    const clear = control.valueChanges.subscribe(() => {
      clear.unsubscribe();
      control.updateValueAndValidity();
    });
  }

  return unclaimed;
}

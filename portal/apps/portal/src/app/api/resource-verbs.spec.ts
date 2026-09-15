import { ApiRequest, ApiResponse, ApiTransport, CyberCloudApi, apiVersion } from '@cybercloud/api';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  ResourceAddress,
  deleteResource,
  listResources,
  parentCountOf,
  pascal,
  putResource,
  readResource,
  verbsFor
} from './resource-verbs';

/** The real document again — see `libs/resource-forms/src/lib/forms-document.spec.ts` for why it is read, not imported. */
interface FormsDocument {
  readonly forms: Readonly<Record<string, { readonly title: string; readonly resourceType: string }>>;
}

const document = JSON.parse(
  readFileSync(join(__dirname, '..', '..', '..', '..', '..', '..', 'generated', 'forms', `${apiVersion}.json`), 'utf8')
) as FormsDocument;

const forms = Object.values(document.forms);

/** A transport that records the one request it was given and answers with nothing. */
class Recorder implements ApiTransport {
  readonly requests: ApiRequest[] = [];

  send<T>(request: ApiRequest): Promise<ApiResponse<T>> {
    this.requests.push(request);
    return Promise.resolve({ status: 200, value: undefined as T });
  }
}

/**
 * The convention that joins two generated surfaces, pinned.
 *
 * `TypeScriptEmitter` names a verb from the type's display name; `FormsEmitter` writes that
 * display name as the form's `title`. Neither knows about the other, and nothing in the .NET
 * build asserts the two agree. This suite does: for every type in the document, the verbs derived
 * from its title must exist on the generated client, and calling one must build the path
 * docs/plan/06 § Identifiers specifies for that type. A title the two emitters spell differently —
 * or a `Pascal` that drifts from `SdkEmitter.Pascal` — fails here rather than in a click.
 */
describe('resource-verbs — the form title names the generated verbs', () => {
  it('mirrors SdkEmitter.Pascal', () => {
    expect(pascal('ClickHouse cluster')).toBe('ClickHouseCluster');
    expect(pascal('Public IP address')).toBe('PublicIPAddress');
    expect(pascal('PostgreSQL server')).toBe('PostgreSQLServer');
    expect(pascal('node-pool')).toBe('NodePool');
    expect(pascal('')).toBe('Value');
    expect(pascal('3 tier')).toBe('N3Tier');
  });

  it.each(forms.map(f => [f.title, f] as const))('%s — every verb exists on CyberCloudApi', (_title, form) => {
    const verbs = verbsFor(form.title);
    const missing = Object.values(verbs).filter(
      name => typeof (CyberCloudApi.prototype as unknown as Record<string, unknown>)[name] !== 'function'
    );

    expect(missing).toEqual([]);
  });

  it.each(forms.map(f => [f.title, f] as const))(
    '%s — GET, PUT, DELETE and list build the identifier path',
    async (_title, form) => {
      const recorder = new Recorder();
      const api = new CyberCloudApi(recorder);
      const verbs = verbsFor(form.title);
      const parents = Array.from({ length: parentCountOf(form.resourceType) }, (_, i) => `parent-${i}`);
      const address: ResourceAddress = {
        tenantId: 't-1',
        subscriptionId: 's-1',
        resourceGroup: 'rg',
        parents,
        name: 'the-one'
      };

      const [provider, ...segments] = form.resourceType.split('/');
      const interleaved = segments.flatMap((segment, i) => [segment, i < segments.length - 1 ? parents[i] : 'the-one']);
      const expected = `/tenants/t-1/subscriptions/s-1/resourceGroups/rg/providers/${provider}/${interleaved.join('/')}`;

      await readResource(api, verbs, address);
      await putResource(api, verbs, address, { location: 'eu-central' });
      await deleteResource(api, verbs, address);
      await listResources(api, verbs, address, { top: 10 });

      expect(recorder.requests.map(r => [r.method, r.path])).toEqual([
        ['GET', expected],
        ['PUT', expected],
        ['DELETE', expected],
        ['GET', expected.slice(0, expected.lastIndexOf('/'))]
      ]);

      expect(recorder.requests[1].body).toEqual({ location: 'eu-central' });
      expect(recorder.requests[3].query).toEqual({ $top: '10' });
    }
  );

  it('encodes a name so a slash cannot forge a path', async () => {
    const recorder = new Recorder();
    const api = new CyberCloudApi(recorder);
    const widget = forms.find(f => f.resourceType === 'CyberCloud.Sample/widgets');
    if (widget === undefined) throw new Error('the sample widget is the fixture every suite leans on');

    await readResource(api, verbsFor(widget.title), {
      tenantId: 't',
      subscriptionId: 's',
      resourceGroup: 'rg',
      parents: [],
      name: 'a/../b'
    });

    expect(recorder.requests[0].path.endsWith('/widgets/a%2F..%2Fb')).toBe(true);
  });

  it('names the disagreement rather than calling undefined', () => {
    const api = new CyberCloudApi(new Recorder());

    expect(() =>
      readResource(api, verbsFor('No such type'), {
        tenantId: 't',
        subscriptionId: 's',
        resourceGroup: 'rg',
        parents: [],
        name: 'x'
      })
    ).toThrow(/getNoSuchType/);
  });
});

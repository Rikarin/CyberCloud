{{/*
The Kubernetes object's name.

⚠ IT IS THE RELEASE NAME, AND THE JOINING THAT MAKES IT UNIQUE HAPPENS BEFORE THIS CHART SEES IT.
`StorageBuckets.ObjectNameOf` is `{accountName}-{bucketName}` and the reconciler computes it from
the resource's ADDRESS, because that is the only place the account's name lives — docs/plan/12
§ Child resources makes the parent a pure function of the address. So this template is `default
.Release.Name .Values.nameOverride`, exactly as charts/managed/seaweedfs' is, and the fact that a
child's object name is qualified is stated in `seaweedfs-bucket.bucketName` below rather than
recomputed here from two values that would then be a third copy of one fact.

⚠ WHY IT MUST BE QUALIFIED AT ALL, since nothing here shows it: `ReconcileDriver.NamespaceFor` is
`{subscriptionId:N}-{resourceGroup}`, so a parent RESOURCE lives inside a namespace rather than being
one. Two accounts in one resource group may each hold a bucket called `assets`, and a renderer that
named the object for the bucket alone would have the two fighting over one Bucket, each converging by
overwriting the other, with neither reporting an error anywhere.
*/}}
{{- define "seaweedfs-bucket.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The bucket's own name — the string an S3 client addresses, and NOT the object's name.

⚠ THE TWO DIFFER ON THIS CHART AND ON NO OTHER IN THE TREE. `spec.name` is what a tenant PUTs
objects into; `metadata.name` is qualified by the account so it is unique in the namespace. Rendering
the qualified name into `spec.name` would hand the tenant a bucket at an address neither they nor
docs/plan/15 § The resource model named.

⚠ `platform.bucketName` and `platform.parentName` are the resource's ADDRESS projected into values,
and they are @internal for that reason: a tenant does not set them, and neither is part of the
resource body. They are also the chart-side twin of the gap
charts/managed/seaweedfs-bucket/conformance.yaml records as `forms-cannot-address-a-child` — a
surface that renders from a body alone has nowhere to put a child's parent, and here the fix was to
project the address in.

⚠ Neither has a `required` guard, which is the tree's convention rather than an oversight: every
`platform.*` value in every managed chart defaults to empty, and `helm lint --strict` runs a chart
against its own defaults, so a `required` on a value the RECONCILER always supplies fails the gate on
every run and teaches nobody anything. The refusal lives in C# where the fact does —
`StorageBuckets.ObjectNameOf` throws on an id with no parent name, and `ResourceId` itself enforces
`ParentNames.Count == Type.Depth - 1` on construction and on `with`, so it cannot be reached.
*/}}
{{- define "seaweedfs-bucket.bucketName" -}}
{{- default .Release.Name .Values.platform.bucketName | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, and a
CHILD type path has TWO of them — `cybercloud.storage_accounts_buckets` — so this is the first chart
on which the replacement has to be a replace-ALL rather than incidentally correct. Helm's `replace`
already is one; stating it is the point, because a single-replacement spelling would render
`cybercloud.storage_accounts/buckets` and be refused at admission, per object, rather than at lint.
*/}}
{{- define "seaweedfs-bucket.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

; Unshipped analyzer
release ; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

 Rule ID | Category                 | Severity | Notes                                                                                                          
---------|--------------------------|----------|----------------------------------------------------------------------------------------------------------------
 CC1001  | CyberCloud.Async         | Warning  | Do not block on a Task from a synchronous method — docs/plan/00 § Coding standards                             
 CC1002  | CyberCloud.Async         | Warning  | Do not declare an async void member — docs/plan/00 § Coding standards                                          
 CC1003  | CyberCloud.Serialization | Warning  | A [GenerateSerializer] type needs a stable [Alias] — docs/plan/04 § Failure and upgrade                        
 CC1004  | CyberCloud.Tenancy       | Warning  | A grain key is built by GrainKeys, never by a literal containing the tenant separator — docs/plan/02 § ADR-002 
 CC1005  | CyberCloud.Security      | Warning  | A secret must not be an [Id]-annotated member of grain state — docs/plan/00 § Non-negotiables                  
 CC1006  | CyberCloud.Security      | Warning  | Outside grain code, take a grain reference through ForTenant — docs/plan/00 § Non-negotiables                  
 CC1007  | CyberCloud.Hygiene       | Warning  | #pragma warning disable needs a linked issue — docs/plan/00 § Non-negotiables                                  

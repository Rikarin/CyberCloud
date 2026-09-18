SELECT `resourceId` AS `resourceId`, `name` AS `name`, `type` AS `type`, `provider` AS `provider`, `location` AS `location`, `resourceGroup` AS `resourceGroup`, `subscriptionId` AS `subscriptionId`, `apiVersion` AS `apiVersion`, `provisioningState` AS `provisioningState`, `clusterId` AS `clusterId`, `tags` AS `tags`, `createdAt` AS `createdAt`, `modifiedAt` AS `modifiedAt`, `version` AS `version`, `change` AS `change` FROM (SELECT toString(resource_id) AS `resourceId`, name AS `name`, concat(provider, '/', type) AS `type`, provider AS `provider`, location AS `location`, resource_group AS `resourceGroup`, toString(subscription_id) AS `subscriptionId`, api_version AS `apiVersion`, provisioning_state AS `provisioningState`, toString(cluster_id) AS `clusterId`, tags AS `tags`, created_at AS `createdAt`, modified_at AS `modifiedAt`, version AS `version`, change AS `change` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) ORDER BY created_at DESC LIMIT 100) WHERE (`location` = {p0:String}) ORDER BY `createdAt` DESC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = eu-central

-- columns
-- resourceId:string, name:string, type:string, provider:string, location:string, resourceGroup:string, subscriptionId:string, apiVersion:string, provisioningState:string, clusterId:string, tags:dynamic, createdAt:datetime, modifiedAt:datetime, version:long, change:string

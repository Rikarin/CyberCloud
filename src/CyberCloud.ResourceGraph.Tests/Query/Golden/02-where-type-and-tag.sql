SELECT toString(resource_id) AS `resourceId`, name AS `name`, concat(provider, '/', type) AS `type`, provider AS `provider`, location AS `location`, resource_group AS `resourceGroup`, toString(subscription_id) AS `subscriptionId`, api_version AS `apiVersion`, provisioning_state AS `provisioningState`, toString(cluster_id) AS `clusterId`, tags AS `tags`, created_at AS `createdAt`, modified_at AS `modifiedAt`, version AS `version`, change AS `change` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((lowerUTF8(concat(provider, '/', type)) = lowerUTF8({p0:String})) AND (tags[{p1:String}] = {p2:String})) ORDER BY toString(resource_id) ASC, name ASC, concat(provider, '/', type) ASC, provider ASC, location ASC, resource_group ASC, toString(subscription_id) ASC, api_version ASC, provisioning_state ASC, toString(cluster_id) ASC, toJSONString(tags) ASC, created_at ASC, modified_at ASC, version ASC, change ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = CyberCloud.DBforPostgreSQL/servers
-- p1:String = env
-- p2:String = prod

-- columns
-- resourceId:string, name:string, type:string, provider:string, location:string, resourceGroup:string, subscriptionId:string, apiVersion:string, provisioningState:string, clusterId:string, tags:dynamic, createdAt:datetime, modifiedAt:datetime, version:long, change:string

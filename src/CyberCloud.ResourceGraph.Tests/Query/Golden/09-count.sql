SELECT count() AS `Count` FROM (SELECT toString(resource_id) AS `resourceId`, name AS `name`, concat(provider, '/', type) AS `type`, provider AS `provider`, location AS `location`, resource_group AS `resourceGroup`, toString(subscription_id) AS `subscriptionId`, api_version AS `apiVersion`, provisioning_state AS `provisioningState`, toString(cluster_id) AS `clusterId`, tags AS `tags`, created_at AS `createdAt`, modified_at AS `modifiedAt`, version AS `version`, change AS `change` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND (toString(subscription_id) = {p0:String})) ORDER BY count() ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = 33333333-3333-4333-8333-333333333333

-- columns
-- Count:long

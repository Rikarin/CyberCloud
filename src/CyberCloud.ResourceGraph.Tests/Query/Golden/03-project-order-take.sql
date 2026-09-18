SELECT `name` AS `name`, `loc` AS `loc`, `resourceGroup` AS `resourceGroup` FROM (SELECT name AS `name`, lowerUTF8(location) AS `loc`, resource_group AS `resourceGroup` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND (location = {p0:String}) ORDER BY name ASC LIMIT 5) ORDER BY `name` ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = eu-central

-- columns
-- name:string, loc:string, resourceGroup:string

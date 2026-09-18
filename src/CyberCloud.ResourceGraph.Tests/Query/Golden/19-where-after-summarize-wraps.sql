SELECT `resourceGroup` AS `resourceGroup`, `n` AS `n` FROM (SELECT resource_group AS `resourceGroup`, count() AS `n` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) GROUP BY resource_group) WHERE (`n` > {p0:Int64}) ORDER BY `n` DESC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:Int64 = 10

-- columns
-- resourceGroup:string, n:long

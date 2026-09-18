SELECT name AS `name`, concat(provider, '/', type) AS `type` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND (provider = {p0:String}) ORDER BY name ASC, concat(provider, '/', type) ASC LIMIT 51 OFFSET 100

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = CyberCloud.Storage

-- columns
-- name:string, type:string

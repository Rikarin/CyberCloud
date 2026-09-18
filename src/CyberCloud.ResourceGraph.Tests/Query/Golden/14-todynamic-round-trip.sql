SELECT name AS `name` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((JSONExtract(toJSONString(tags), 'Map(String, String)')[{p0:String}] = {p1:String}) AND (tags[{p2:String}] = {p3:String})) ORDER BY name ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = env
-- p1:String = prod
-- p2:String = env
-- p3:String = prod

-- columns
-- name:string

SELECT name AS `name`, tags AS `tags` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND match(toJSONString(tags), {p0:String}) ORDER BY name ASC, toJSONString(tags) ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = (?i)(^|[^\p{L}\p{N}_])prod($|[^\p{L}\p{N}_])

-- columns
-- name:string, tags:dynamic

SELECT name AS `name`, tags AS `tags` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((((tags[{p0:String}] = '') AND (tags[{p1:String}] != '')) AND NOT (empty(tags))) AND (location != '')) ORDER BY name ASC, toJSONString(tags) ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = env
-- p1:String = owner

-- columns
-- name:string, tags:dynamic

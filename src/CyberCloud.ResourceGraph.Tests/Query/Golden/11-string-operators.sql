SELECT name AS `name` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((((match(name, {p0:String}) OR (positionCaseInsensitiveUTF8(name, {p1:String}) > 0)) OR startsWith(lowerUTF8(name), lowerUTF8({p2:String}))) OR endsWith(lowerUTF8(name), lowerUTF8({p3:String}))) OR (lowerUTF8(concat(provider, '/', type)) != lowerUTF8({p4:String}))) ORDER BY name ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = (?i)(^|[^\p{L}\p{N}_])db($|[^\p{L}\p{N}_])
-- p1:String = cache
-- p2:String = pg-
-- p3:String = -prod
-- p4:String = cybercloud.sample/widgets

-- columns
-- name:string

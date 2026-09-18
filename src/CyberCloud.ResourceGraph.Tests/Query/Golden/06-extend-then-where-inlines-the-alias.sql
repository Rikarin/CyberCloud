SELECT lowerUTF8(name) AS `shortName`, tags[{p0:String}] AS `owner` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((tags[{p0:String}] = {p1:String}) AND startsWith(lowerUTF8(lowerUTF8(name)), lowerUTF8({p2:String}))) ORDER BY lowerUTF8(name) ASC, tags[{p0:String}] ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = owner
-- p1:String = platform-team
-- p2:String = pg-

-- columns
-- shortName:string, owner:string

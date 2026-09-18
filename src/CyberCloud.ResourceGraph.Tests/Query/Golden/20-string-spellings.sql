SELECT concat(provider, '/', type) AS `type` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((((name = {p0:String}) OR (name = {p1:String})) OR (name = {p2:String})) OR (resource_group = {p3:String})) GROUP BY concat(provider, '/', type) ORDER BY concat(provider, '/', type) ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = double-quoted
-- p1:String = its
-- p2:String = back\slash
-- p3:String = 

-- columns
-- type:string

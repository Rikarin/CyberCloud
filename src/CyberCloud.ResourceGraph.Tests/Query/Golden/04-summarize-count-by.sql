SELECT concat(provider, '/', type) AS `type`, location AS `location`, count() AS `count_` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) GROUP BY concat(provider, '/', type), location ORDER BY count() DESC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']

-- columns
-- type:string, location:string, count_:long

SELECT DISTINCT concat(provider, '/', type) AS `type`, location AS `location` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND (provisioning_state != {p0:String}) ORDER BY concat(provider, '/', type) ASC, location ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = Failed

-- columns
-- type:string, location:string

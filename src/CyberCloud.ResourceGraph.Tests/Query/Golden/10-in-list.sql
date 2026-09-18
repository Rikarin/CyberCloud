SELECT name AS `name`, location AS `location`, version AS `version` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((location IN ({p0:String}, {p1:String})) AND (version IN ({p2:Int64}, {p3:Int64}, {p4:Int64}))) ORDER BY name ASC, location ASC, version ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = eu-central
-- p1:String = eu-west
-- p2:Int64 = 1
-- p3:Int64 = 2
-- p4:Int64 = 3

-- columns
-- name:string, location:string, version:long

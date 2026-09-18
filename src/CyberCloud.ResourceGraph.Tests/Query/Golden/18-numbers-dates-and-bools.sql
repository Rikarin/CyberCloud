SELECT name AS `name`, toBool((provisioning_state = {p6:String})) AS `live` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND ((((((created_at >= {p0:DateTime64(3)}) AND (modified_at < {p1:DateTime64(3)})) AND (version > {p2:Int64})) AND (version <= {p3:Int64})) AND (version != {p4:Float64})) AND (version >= {p5:Int64})) AND ((provisioning_state = {p6:String}) = {p7:Bool}) ORDER BY name ASC, (provisioning_state = {p6:String}) ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:DateTime64(3) = 2026-09-17 10:00:00.000
-- p1:DateTime64(3) = 2026-09-18 00:00:00.000
-- p2:Int64 = 2
-- p3:Int64 = 10
-- p4:Float64 = 1.5
-- p5:Int64 = -1
-- p6:String = Succeeded
-- p7:Bool = true

-- columns
-- name:string, live:bool

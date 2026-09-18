SELECT concat(name, {p3:String}, toString(version), {p4:String}, location, {p5:String}, toJSONString(tags)) AS `label`, arrayElement(splitByString({p0:String}, name), {p1:Int64} + 1) AS `family`, splitByString({p2:String}, name) AS `parts`, toString(version) AS `versionText`, toJSONString(tags) AS `tagText` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) ORDER BY concat(name, {p3:String}, toString(version), {p4:String}, location, {p5:String}, toJSONString(tags)) ASC, arrayElement(splitByString({p0:String}, name), {p1:Int64} + 1) ASC, toJSONString(splitByString({p2:String}, name)) ASC, toString(version) ASC, toJSONString(tags) ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = -
-- p1:Int64 = 0
-- p2:String = -
-- p3:String =  v
-- p4:String =  in 
-- p5:String =  

-- columns
-- label:string, family:string, parts:dynamic, versionText:string, tagText:string

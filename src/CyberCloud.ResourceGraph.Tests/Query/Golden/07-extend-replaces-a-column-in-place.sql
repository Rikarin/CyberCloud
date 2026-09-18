SELECT toString(resource_id) AS `resourceId`, upperUTF8(name) AS `name`, lowerUTF8(provider) AS `Column1` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) AND (upperUTF8(name) = {p0:String}) ORDER BY toString(resource_id) ASC, upperUTF8(name) ASC, lowerUTF8(provider) ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']
-- p0:String = MAIN

-- columns
-- resourceId:string, name:string, Column1:string

SELECT lowerUTF8(location) AS `region`, provider AS `provider`, uniqExact(name) AS `names`, min(version) AS `min_version`, max(version) AS `max_version`, sum(version) AS `total`, avg(version) AS `avg_version` FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE is_deleted = 0 AND hasAny(access, {access:Array(String)}) GROUP BY lowerUTF8(location), provider ORDER BY lowerUTF8(location) ASC, provider ASC, uniqExact(name) ASC, min(version) ASC, max(version) ASC, sum(version) ASC, avg(version) ASC LIMIT 51

-- parameters
-- access:Array(String) = ['user:alice','group:eng#member']

-- columns
-- region:string, provider:string, names:long, min_version:long, max_version:long, total:long, avg_version:real

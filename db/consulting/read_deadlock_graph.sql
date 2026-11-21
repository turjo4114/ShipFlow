-- Pull recent deadlock graphs from the always-on system_health session (no tracing needed).
-- In SSMS/Azure Data Studio click the XML cell to see the graph.
;WITH x AS (
    SELECT CAST(st.target_data AS XML) AS td
    FROM sys.dm_xe_session_targets st
    JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
    WHERE s.name = N'system_health' AND st.target_name = N'ring_buffer')
SELECT q.d.value('(@timestamp)[1]', 'datetime2')                        AS EventTimeUtc,
       q.d.query('(data[@name="xml_report"]/value/deadlock)[1]')        AS DeadlockGraph
FROM x CROSS APPLY td.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS q(d)
ORDER BY EventTimeUtc DESC;

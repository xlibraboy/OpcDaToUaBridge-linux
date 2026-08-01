# InfluxDB (Historical Logging)

- External InfluxDB 2.x/3.x server required
- Configure URL, Org, Bucket, Token on InfluxDB tab; Save + Connect
- Enable per tag via faceplate Influx checkbox
- Points: measurement opc_tags (configurable), tags source_id/da_item_id/display_name, fields value/quality/is_good
- Outage does not stop the bridge

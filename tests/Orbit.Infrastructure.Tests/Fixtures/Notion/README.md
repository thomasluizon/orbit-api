These JSON files are copied from the live Notion developer documentation's examples.

- `page.json`: [Page object example](https://developers.notion.com/reference/page).
- `*-schema.json` and `*-page-value.json`: the Schema and Page value tabs under Title, Date, Status and Checkbox in [Page property values](https://developers.notion.com/reference/page-property-values).

Tests combine these recorded properties with the recorded page envelope, and vary values to exercise dates, custom labels, malicious text, pagination and the task cap. Data source envelopes use the documented fields and example identifiers in [Data source](https://developers.notion.com/reference/data-source). List envelopes follow [Pagination](https://developers.notion.com/reference/intro#pagination). OAuth fixtures follow the complete response schema in [Create a token](https://developers.notion.com/reference/create-a-token), whose refresh token and workspace name are nullable. Rate-limit responses follow [Request limits](https://developers.notion.com/reference/request-limits).

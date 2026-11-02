-- health.orphan_action was renamed to health.unowned_action; keep the stored choice.
UPDATE config SET value = replace(value, '"orphan_action":', '"unowned_action":') WHERE key = 'health';

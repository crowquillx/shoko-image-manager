(() => {
  'use strict';

  const API_ROOT = '/api/v3';
  const PLUGIN_ID = '7c5f8f4d-7b1d-4e07-9d4c-5d8bd6f581a2';
  const SECRET_FIELDS = ['FanartTvApiKey', 'FanartTvClientKey'];
  const CONFIG_FIELDS = [
    ['Enabled', 'boolean'],
    ['PreferredLanguage', 'string'],
    ['FanartTvPriority', 'number'],
    ['RequestTimeoutSeconds', 'number'],
    ['MaxJsonResponseBytes', 'number'],
    ['MaxImageBytes', 'number'],
    ['RecurringReconciliationEnabled', 'boolean'],
    ['ReconciliationIntervalMinutes', 'number'],
    ['IdempotencyReceiptRetentionDays', 'number'],
  ];
  const state = {
    configId: '',
    config: {},
    schema: {},
    secrets: {},
  };

  const getElement = id => document.getElementById(id);

  const setText = (id, value) => {
    const element = getElement(id);
    if (element) element.textContent = value;
  };

  const showMessage = (message, kind = '') => {
    const element = getElement('page-message');
    if (!element) return;
    element.textContent = message;
    element.className = kind === '' ? 'message' : `message ${kind}`;
  };

  const showSaveMessage = (message, kind = '') => {
    const element = getElement('save-message');
    if (!element) return;
    element.textContent = message;
    element.className = kind;
  };

  const setFormBusy = busy => {
    const form = getElement('configuration-form');
    if (form) form.setAttribute('aria-busy', String(busy));
  };

  const parseObject = value => {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) return null;
    return value;
  };

  const readStorageObject = (storage, key) => {
    try {
      const value = storage.getItem(key);
      return value === null ? null : parseObject(JSON.parse(value));
    } catch {
      return null;
    }
  };

  const getApiKey = () => {
    try {
      const stateObject = readStorageObject(globalThis.sessionStorage, 'state');
      const sessionKey = stateObject?.apiSession?.apikey;
      if (typeof sessionKey === 'string' && sessionKey.length > 0) return sessionKey;

      const rememberedSession = readStorageObject(globalThis.localStorage, 'apiSession');
      const rememberedKey = rememberedSession?.apikey;
      return typeof rememberedKey === 'string' && rememberedKey.length > 0 ? rememberedKey : '';
    } catch {
      return '';
    }
  };

  const requestJson = async (path, options = {}) => {
    const apiKey = getApiKey();
    if (apiKey === '') throw new Error('No WebUI API key is available. Sign in again and reload this page.');

    const headers = new Headers(options.headers);
    headers.set('Accept', 'application/json');
    headers.set('apikey', apiKey);
    if (options.body !== undefined) headers.set('Content-Type', 'application/json');

    const response = await fetch(path, {
      ...options,
      credentials: 'same-origin',
      headers,
    });
    if (!response.ok) throw new Error(`Shoko returned HTTP ${response.status}.`);
    return response.json();
  };

  const propertyName = (object, name) =>
    Object.keys(object).find(key => key.toLowerCase() === name.toLowerCase()) ?? name;

  const propertyValue = (object, name) => {
    if (object === null || typeof object !== 'object') return undefined;
    const key = Object.keys(object).find(candidate => candidate.toLowerCase() === name.toLowerCase());
    return key === undefined ? undefined : object[key];
  };

  const setProperty = (object, name, value) => {
    object[propertyName(object, name)] = value;
  };

  const getConfigEntryId = entry => {
    const id = propertyValue(entry, 'ID');
    return typeof id === 'string' ? id : '';
  };

  const populateField = (name, type, config) => {
    const element = getElement(name);
    if (!element) return;
    const value = propertyValue(config, name);
    if (type === 'boolean') {
      element.checked = value === true;
    } else if (value !== undefined && value !== null) {
      element.value = String(value);
    }
  };

  const applySchemaLimits = schema => {
    const properties = parseObject(propertyValue(schema, 'properties'));
    if (!properties) return;
    for (const [name] of CONFIG_FIELDS) {
      const element = getElement(name);
      const rules = parseObject(propertyValue(properties, name));
      if (!element || !rules) continue;
      const minimum = propertyValue(rules, 'minimum');
      const maximum = propertyValue(rules, 'maximum');
      const maxLength = propertyValue(rules, 'maxLength');
      if (typeof minimum === 'number') element.min = String(minimum);
      if (typeof maximum === 'number') element.max = String(maximum);
      if (typeof maxLength === 'number') element.maxLength = maxLength;
    }
  };

  const populateConfiguration = (config, schema) => {
    for (const [name, type] of CONFIG_FIELDS) populateField(name, type, config);
    for (const name of SECRET_FIELDS) {
      const value = propertyValue(config, name);
      state.secrets[name] = typeof value === 'string' ? value : '';
      const field = getElement(name);
      const help = getElement(`${name}-help`);
      if (field) field.value = '';
      const clearField = getElement(`${name}Clear`);
      if (clearField) clearField.checked = false;
      if (help) help.textContent = state.secrets[name] === '' ? 'No stored key. Enter a key to configure this provider.' : 'Stored value is present. Leave blank to keep it.';
    }
    applySchemaLimits(schema);
  };

  const renderStatus = status => {
    const enabled = propertyValue(status, 'Enabled') === true;
    const version = propertyValue(status, 'PluginVersion');
    const apiVersion = propertyValue(status, 'ApiVersion');
    const versionText = typeof version === 'string' ? ` Plugin version ${version}.` : '';
    const apiText = typeof apiVersion === 'string' ? ` API version ${apiVersion}.` : '';
    setText('status-summary', enabled ? `Planner is enabled.${versionText}${apiText}` : `Planner is disabled.${versionText}${apiText}`);

    const list = getElement('capabilities-list');
    if (!list) return;
    list.replaceChildren();
    const capabilities = propertyValue(status, 'Capabilities');
    if (!Array.isArray(capabilities) || capabilities.length === 0) {
      const empty = document.createElement('li');
      empty.textContent = 'No capabilities were reported.';
      list.append(empty);
      return;
    }
    for (const capability of capabilities) {
      const item = document.createElement('li');
      const name = propertyValue(capability, 'Name');
      const capabilityEnabled = propertyValue(capability, 'Enabled') === true;
      const detail = propertyValue(capability, 'Detail');
      item.textContent = `${typeof name === 'string' ? name : 'Capability'}: ${capabilityEnabled ? 'enabled' : 'disabled'}${typeof detail === 'string' && detail !== '' ? ` (${detail})` : ''}`;
      list.append(item);
    }
  };

  const renderProviders = providers => {
    const list = getElement('providers-list');
    if (!list) return;
    list.replaceChildren();
    if (!Array.isArray(providers) || providers.length === 0) {
      const empty = document.createElement('li');
      empty.textContent = 'No providers were reported.';
      list.append(empty);
      return;
    }
    for (const provider of providers) {
      const item = document.createElement('li');
      const name = propertyValue(provider, 'Name');
      const source = propertyValue(provider, 'Source');
      const configured = propertyValue(provider, 'Configured') === true;
      const priority = propertyValue(provider, 'Priority');
      const label = document.createElement('span');
      label.textContent = typeof name === 'string' ? name : 'Provider';
      const details = document.createElement('span');
      details.textContent = `${configured ? 'configured' : 'not configured'}${typeof source === 'string' ? ` · ${source}` : ''}${typeof priority === 'number' ? ` · priority ${priority}` : ''}`;
      item.append(label, details);
      list.append(item);
    }
  };

  const loadConfiguration = async () => {
    const entries = await requestJson(`${API_ROOT}/Configuration?pluginID=${encodeURIComponent(PLUGIN_ID)}`);
    if (!Array.isArray(entries)) throw new Error('Shoko returned an invalid configuration list.');
    const entry = entries.find(candidate => getConfigEntryId(candidate) !== '');
    const configId = entry === undefined ? '' : getConfigEntryId(entry);
    if (configId === '') throw new Error('The Image Planner configuration was not found.');

    const [config, schema] = await Promise.all([
      requestJson(`${API_ROOT}/Configuration/${encodeURIComponent(configId)}`),
      requestJson(`${API_ROOT}/Configuration/${encodeURIComponent(configId)}/Schema`),
    ]);
    const configObject = parseObject(config);
    const schemaObject = parseObject(schema);
    if (!configObject || !schemaObject) throw new Error('Shoko returned an invalid configuration or schema.');

    state.configId = configId;
    state.config = { ...configObject };
    state.schema = schemaObject;
    populateConfiguration(configObject, schemaObject);
    for (const name of SECRET_FIELDS) delete state.config[propertyName(state.config, name)];
  };

  const loadStatus = async () => renderStatus(await requestJson(`${API_ROOT}/Plugin/ImagePlanner/status`));
  const loadProviders = async () => renderProviders(await requestJson(`${API_ROOT}/Plugin/ImagePlanner/providers`));

  const refresh = async () => {
    const refreshButton = getElement('refresh-button');
    if (refreshButton) refreshButton.disabled = true;
    setFormBusy(true);
    showMessage('Loading Image Planner data.');
    try {
      const results = await Promise.allSettled([loadConfiguration(), loadStatus(), loadProviders()]);
      const failure = results.find(result => result.status === 'rejected');
      if (failure && failure.status === 'rejected') {
        showMessage(failure.reason instanceof Error ? failure.reason.message : 'Unable to load Image Planner data.', 'error');
      } else {
        showMessage('Image Planner data is current.', 'success');
      }
    } finally {
      setFormBusy(false);
      if (refreshButton) refreshButton.disabled = false;
    }
  };

  const collectConfiguration = () => {
    const payload = { ...state.config };
    for (const [name, type] of CONFIG_FIELDS) {
      const field = getElement(name);
      if (!field) continue;
      setProperty(payload, name, type === 'boolean' ? field.checked : type === 'number' ? Number(field.value) : field.value);
    }
    for (const name of SECRET_FIELDS) {
      const field = getElement(name);
      const clearField = getElement(`${name}Clear`);
      if (!field) continue;
      const value = field.value.trim();
      if (clearField?.checked) setProperty(payload, name, '');
      else if (value !== '') setProperty(payload, name, value);
      else if (state.secrets[name] !== '') setProperty(payload, name, state.secrets[name]);
    }
    return payload;
  };

  const showValidationErrors = result => {
    const errors = parseObject(propertyValue(result, 'ValidationErrors'));
    if (!errors || Object.keys(errors).length === 0) return false;
    const messages = [];
    for (const [name, values] of Object.entries(errors)) {
      if (Array.isArray(values)) {
        for (const value of values) messages.push(`${name}: ${String(value)}`);
      }
    }
    showSaveMessage(messages.length === 0 ? 'Shoko rejected the configuration.' : messages.join(' '), 'error');
    return true;
  };

  const saveConfiguration = async event => {
    event.preventDefault();
    if (state.configId === '') {
      showSaveMessage('The configuration is not loaded.', 'error');
      return;
    }
    const saveButton = getElement('save-button');
    if (saveButton) saveButton.disabled = true;
    setFormBusy(true);
    showSaveMessage('Saving configuration.');
    const payload = collectConfiguration();
    try {
      const result = await requestJson(`${API_ROOT}/Configuration/${encodeURIComponent(state.configId)}`, {
        method: 'PUT',
        body: JSON.stringify(payload),
      });
      if (!showValidationErrors(result)) {
        for (const name of SECRET_FIELDS) {
          const key = propertyName(payload, name);
          state.secrets[name] = typeof payload[key] === 'string' ? payload[key] : '';
          const field = getElement(name);
          const clearField = getElement(`${name}Clear`);
          if (field) field.value = '';
          if (clearField) clearField.checked = false;
          const help = getElement(`${name}-help`);
          if (help) help.textContent = state.secrets[name] === '' ? 'No stored key. Enter a key to configure this provider.' : 'Stored value is present. Leave blank to keep it.';
          delete payload[key];
        }
        state.config = payload;
        showSaveMessage('Configuration saved. Restart Shoko for the changes to take effect.', 'success');
      }
    } catch (error) {
      showSaveMessage(error instanceof Error ? error.message : 'Unable to save configuration.', 'error');
    } finally {
      setFormBusy(false);
      if (saveButton) saveButton.disabled = false;
    }
  };

  const start = () => {
    const form = getElement('configuration-form');
    const refreshButton = getElement('refresh-button');
    if (form) form.addEventListener('submit', saveConfiguration);
    if (refreshButton) refreshButton.addEventListener('click', refresh);
    refresh();
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();

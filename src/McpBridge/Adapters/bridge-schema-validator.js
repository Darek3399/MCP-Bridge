'use strict';

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function deepEqual(left, right) {
  if (Object.is(left, right)) return true;
  if (Array.isArray(left) && Array.isArray(right)) {
    return left.length === right.length && left.every((item, index) => deepEqual(item, right[index]));
  }
  if (isObject(left) && isObject(right)) {
    const leftKeys = Object.keys(left).sort();
    const rightKeys = Object.keys(right).sort();
    return leftKeys.length === rightKeys.length &&
      leftKeys.every((key, index) => key === rightKeys[index] && deepEqual(left[key], right[key]));
  }
  return false;
}

function resolveLocalReference(root, reference) {
  if (typeof reference !== 'string' || !reference.startsWith('#/')) return null;
  const tokens = reference.slice(2).split('/').map(token =>
    token.replace(/~1/g, '/').replace(/~0/g, '~'));
  let current = root;
  for (const token of tokens) {
    if (!current || typeof current !== 'object' || !(token in current)) return null;
    current = current[token];
  }
  return current;
}

function matchesType(type, value) {
  const types = Array.isArray(type) ? type : [type];
  return types.some(candidate => {
    switch (candidate) {
      case 'null': return value === null;
      case 'object': return isObject(value);
      case 'array': return Array.isArray(value);
      case 'string': return typeof value === 'string';
      case 'boolean': return typeof value === 'boolean';
      case 'integer': return typeof value === 'number' && Number.isInteger(value);
      case 'number': return typeof value === 'number' && Number.isFinite(value);
      default: return true;
    }
  });
}

function validateSchema(schema, value, root, path, errors, depth, referenceStack) {
  if (depth > 64) {
    errors.push(path + ': schema nesting limit exceeded.');
    return;
  }

  if (schema === true || schema == null) return;
  if (schema === false) {
    errors.push(path + ': value is not allowed.');
    return;
  }
  if (!isObject(schema)) return;

  if (typeof schema.$ref === 'string') {
    const resolved = resolveLocalReference(root, schema.$ref);
    if (resolved !== null)
      validateSchema(resolved, value, root, path, errors, depth + 1, referenceStack);
  }

  if (schema.allOf && Array.isArray(schema.allOf)) {
    for (const branch of schema.allOf)
      validateSchema(branch, value, root, path, errors, depth + 1, referenceStack);
  }

  if (schema.anyOf && Array.isArray(schema.anyOf)) {
    const branchErrors = schema.anyOf.map(branch => {
      const local = [];
      validateSchema(branch, value, root, path, local, depth + 1, referenceStack);
      return local;
    });
    if (!branchErrors.some(items => items.length === 0))
      errors.push(path + ': value does not match any allowed schema alternative.');
  }

  if (schema.oneOf && Array.isArray(schema.oneOf)) {
    const validBranches = schema.oneOf.filter(branch => {
      const local = [];
      validateSchema(branch, value, root, path, local, depth + 1, referenceStack);
      return local.length === 0;
    });
    if (validBranches.length !== 1)
      errors.push(path + ': value must match exactly one schema alternative.');
  }

  if (schema.if !== undefined) {
    const conditionErrors = [];
    validateSchema(schema.if, value, root, path, conditionErrors, depth + 1, referenceStack);
    const branch = conditionErrors.length === 0 ? schema.then : schema.else;
    if (branch !== undefined)
      validateSchema(branch, value, root, path, errors, depth + 1, referenceStack);
  }

  if (schema.not) {
    const local = [];
    validateSchema(schema.not, value, root, path, local, depth + 1, referenceStack);
    if (local.length === 0) errors.push(path + ': value matches a forbidden schema.');
  }

  if (schema.const !== undefined && !deepEqual(value, schema.const))
    errors.push(path + ': value does not match const.');
  if (Array.isArray(schema.enum) && !schema.enum.some(item => deepEqual(item, value)))
    errors.push(path + ': value is not one of the allowed enum values.');

  if (schema.type && !matchesType(schema.type, value)) {
    errors.push(path + ': expected type ' + JSON.stringify(schema.type) + '.');
    return;
  }

  if (typeof value === 'string') {
    if (Number.isInteger(schema.minLength) && value.length < schema.minLength)
      errors.push(path + ': string is shorter than minLength ' + schema.minLength + '.');
    if (Number.isInteger(schema.maxLength) && value.length > schema.maxLength)
      errors.push(path + ': string is longer than maxLength ' + schema.maxLength + '.');
    if (typeof schema.pattern === 'string') {
      try {
        if (!(new RegExp(schema.pattern)).test(value))
          errors.push(path + ': string does not match the required pattern.');
      } catch (_) { }
    }
  }

  if (typeof value === 'number' && Number.isFinite(value)) {
    if (typeof schema.minimum === 'number' && value < schema.minimum)
      errors.push(path + ': number is below minimum ' + schema.minimum + '.');
    if (typeof schema.maximum === 'number' && value > schema.maximum)
      errors.push(path + ': number is above maximum ' + schema.maximum + '.');
    if (typeof schema.exclusiveMinimum === 'number' && value <= schema.exclusiveMinimum)
      errors.push(path + ': number must be greater than ' + schema.exclusiveMinimum + '.');
    if (typeof schema.exclusiveMaximum === 'number' && value >= schema.exclusiveMaximum)
      errors.push(path + ': number must be less than ' + schema.exclusiveMaximum + '.');
    if (typeof schema.multipleOf === 'number' && schema.multipleOf > 0) {
      const quotient = value / schema.multipleOf;
      if (Math.abs(quotient - Math.round(quotient)) > 1e-10)
        errors.push(path + ': number must be a multiple of ' + schema.multipleOf + '.');
    }
  }

  if (Array.isArray(value)) {
    if (Number.isInteger(schema.minItems) && value.length < schema.minItems)
      errors.push(path + ': array has fewer than minItems ' + schema.minItems + '.');
    if (Number.isInteger(schema.maxItems) && value.length > schema.maxItems)
      errors.push(path + ': array has more than maxItems ' + schema.maxItems + '.');
    if (schema.uniqueItems === true) {
      for (let i = 0; i < value.length; i++) {
        for (let j = i + 1; j < value.length; j++) {
          if (deepEqual(value[i], value[j])) {
            errors.push(path + ': array items must be unique.');
            i = value.length;
            break;
          }
        }
      }
    }
    if (Array.isArray(schema.prefixItems)) {
      schema.prefixItems.forEach((itemSchema, index) => {
        if (index < value.length)
          validateSchema(itemSchema, value[index], root, path + '[' + index + ']', errors, depth + 1, referenceStack);
      });
      if (schema.items !== undefined) {
        for (let index = schema.prefixItems.length; index < value.length; index++)
          validateSchema(schema.items, value[index], root, path + '[' + index + ']', errors, depth + 1, referenceStack);
      }
    } else if (schema.items !== undefined && !Array.isArray(schema.items)) {
      value.forEach((item, index) =>
        validateSchema(schema.items, item, root, path + '[' + index + ']', errors, depth + 1, referenceStack));
    } else if (Array.isArray(schema.items)) {
      schema.items.forEach((itemSchema, index) => {
        if (index < value.length)
          validateSchema(itemSchema, value[index], root, path + '[' + index + ']', errors, depth + 1, referenceStack);
      });
    }

    if (schema.contains !== undefined) {
      let matches = 0;
      for (let index = 0; index < value.length; index++) {
        const local = [];
        validateSchema(schema.contains, value[index], root, path + '[' + index + ']', local, depth + 1, referenceStack);
        if (local.length === 0) matches++;
      }
      const minContains = Number.isInteger(schema.minContains) ? schema.minContains : 1;
      const maxContains = Number.isInteger(schema.maxContains) ? schema.maxContains : Number.POSITIVE_INFINITY;
      if (matches < minContains || matches > maxContains)
        errors.push(path + ': array does not satisfy contains/minContains/maxContains.');
    }
  }

  if (isObject(value)) {
    const properties = isObject(schema.properties) ? schema.properties : {};
    const patternProperties = isObject(schema.patternProperties) ? schema.patternProperties : {};
    const required = Array.isArray(schema.required) ? schema.required : [];

    if (Number.isInteger(schema.minProperties) && Object.keys(value).length < schema.minProperties)
      errors.push(path + ': object has fewer than minProperties ' + schema.minProperties + '.');
    if (Number.isInteger(schema.maxProperties) && Object.keys(value).length > schema.maxProperties)
      errors.push(path + ': object has more than maxProperties ' + schema.maxProperties + '.');

    for (const name of required) {
      if (typeof name === 'string' && !Object.prototype.hasOwnProperty.call(value, name))
        errors.push(path + '.' + name + ': required property is missing.');
    }

    for (const [name, propertyValue] of Object.entries(value)) {
      const propertyPath = path + '.' + name;
      const hasExplicitProperty = Object.prototype.hasOwnProperty.call(properties, name);
      if (hasExplicitProperty)
        validateSchema(properties[name], propertyValue, root, propertyPath, errors, depth + 1, referenceStack);

      let patternMatched = false;
      for (const [pattern, patternSchema] of Object.entries(patternProperties)) {
        try {
          if (new RegExp(pattern).test(name)) {
            patternMatched = true;
            validateSchema(patternSchema, propertyValue, root, propertyPath, errors, depth + 1, referenceStack);
          }
        } catch (_) { }
      }

      if (!hasExplicitProperty && !patternMatched) {
        if (schema.additionalProperties === false) {
          errors.push(propertyPath + ': additional property is not allowed.');
        } else if (schema.additionalProperties !== undefined && schema.additionalProperties !== true) {
          validateSchema(schema.additionalProperties, propertyValue, root, propertyPath, errors, depth + 1, referenceStack);
        }
      }

      if (schema.propertyNames !== undefined)
        validateSchema(schema.propertyNames, name, root, propertyPath, errors, depth + 1, referenceStack);
    }

    if (isObject(schema.dependentSchemas)) {
      for (const [trigger, dependentSchema] of Object.entries(schema.dependentSchemas)) {
        if (Object.prototype.hasOwnProperty.call(value, trigger))
          validateSchema(dependentSchema, value, root, path, errors, depth + 1, referenceStack);
      }
    }

    if (isObject(schema.dependentRequired)) {
      for (const [trigger, dependencies] of Object.entries(schema.dependentRequired)) {
        if (Object.prototype.hasOwnProperty.call(value, trigger) && Array.isArray(dependencies)) {
          for (const dependency of dependencies) {
            if (!Object.prototype.hasOwnProperty.call(value, dependency))
              errors.push(path + '.' + dependency + ': required when ' + trigger + ' is present.');
          }
        }
      }
    }
  }
}

function validate(schema, value) {
  if (!isObject(schema)) return [];
  const errors = [];
  validateSchema(schema, value, schema, '$', errors, 0, new Set());
  return errors;
}

module.exports = { validate };